using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;

namespace AriaGui.Infrastructure.Aria2;

public sealed partial class Aria2ProcessManager
{
    public bool SupportsLegacyTransition => _options.UseContinuityGateway;

    // Explicit user action only. This deliberately does not adopt, restart, kill,
    // delete files, or make direct transfer sources routable through the gateway.
    public async Task StopLegacyDirectAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!SupportsLegacyTransition || _repository is null || _process is not null)
                throw LegacyRefused();
            UserRuntimeDirectory.EnsurePrivate(_options.RuntimeDirectory);
            await AcquireLeaseAsync(token).ConfigureAwait(false);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                await VerifyBinaryAsync(deadline.Token).ConfigureAwait(false);
                await _repository.InitializeAsync(deadline.Token).ConfigureAwait(false);
                using var ownerFile = OpenPrivateMetadata(OwnershipPath, 16384);
                var owner = await JsonSerializer.DeserializeAsync<EngineOwnership>(ownerFile, cancellationToken: deadline.Token).ConfigureAwait(false);
                if (owner is null || owner.Guarded || owner.SessionId is null || owner.SessionFile is null
                    || owner.RpcPort != _options.RpcPort || !SessionRecoveryPlanner.IsSessionPath(_options.RuntimeDirectory, owner.SessionFile)
                    || !Path.IsPathFullyQualified(owner.ExecutablePath) || owner.ExecutablePath.IndexOfAny(['\r', '\n']) >= 0) throw LegacyRefused();
                using var process = Process.GetProcessById(owner.ProcessId);
                RequireLegacyProcess(process, owner);
                // A package update may move the executable. Its old recorded path
                // is accepted only for the identical pinned bundle, never arbitrary
                // previous versions or an executable selected by request JSON.
                if ((File.GetAttributes(owner.ExecutablePath) & FileAttributes.ReparsePoint) != 0) throw LegacyRefused();
                using var image = new FileStream(owner.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Convert.ToHexString(await SHA256.HashDataAsync(image, deadline.Token).ConfigureAwait(false)) != _options.ExpectedSha256) throw LegacyRefused();
                try
                {
                    using var previous = Process.GetProcessById(owner.AppProcessId);
                    if (!previous.HasExited && (owner.AppStartedAtUtc is null || previous.StartTime.ToUniversalTime() == owner.AppStartedAtUtc)) throw LegacyRefused();
                }
                catch (ArgumentException) { }
                using var config = OpenPrivateMetadata(ConfigPath, 65536);
                using var reader = new StreamReader(config);
                var settings = (await reader.ReadToEndAsync(deadline.Token).ConfigureAwait(false)).Split('\n')
                    .Where(line => line.Contains('=')).ToDictionary(line => line[..line.IndexOf('=')].Trim(), line => line[(line.IndexOf('=') + 1)..].TrimEnd('\r'), StringComparer.Ordinal);
                if (!settings.TryGetValue("rpc-secret", out var secret) || secret.Length != 64 || !secret.All(Uri.IsHexDigit)
                    || !settings.TryGetValue("rpc-listen-all", out var all) || all != "false"
                    || !settings.TryGetValue("rpc-allow-origin-all", out var origin) || origin != "false"
                    || !settings.TryGetValue("rpc-listen-port", out var port) || port != _options.RpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || settings.ContainsKey("stop-with-process") || !settings.TryGetValue("save-session", out var session) || session != owner.SessionFile) throw LegacyRefused();
                var rpc = new Aria2RpcClient(_http, new($"http://127.0.0.1:{owner.RpcPort}/jsonrpc"), secret, _options.RequestTimeout);
                var info = await rpc.GetInfoAsync(deadline.Token).ConfigureAwait(false);
                if (info.SessionId != owner.SessionId || info.Version != Aria2ProcessOptions.BundledVersion) throw LegacyRefused();
                var inventory = await rpc.GetInventoryAsync(deadline.Token, 4096).ConfigureAwait(false);
                var entries = new Dictionary<string, DownloadEntry>(StringComparer.Ordinal);
                // Validate every row before the first mutation, including historical stopped results.
                foreach (var gid in inventory)
                {
                    var entry = await _repository.GetAttemptAsync(gid, deadline.Token).ConfigureAwait(false);
                    var observed = await rpc.FindAsync(gid, deadline.Token).ConfigureAwait(false);
                    if (entry is null || observed is null || !await MatchesLegacyEntryAsync(rpc, entry, observed, deadline.Token).ConfigureAwait(false)) throw LegacyRefused();
                    entries.Add(gid, entry);
                }
                RequireLegacyProcess(process, owner);
                foreach (var gid in inventory)
                {
                    var observed = await rpc.FindAsync(gid, deadline.Token).ConfigureAwait(false) ?? throw LegacyRefused();
                    if (observed.Progress.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
                    {
                        RequireLegacyProcess(process, owner);
                        await rpc.ExecuteAsync(gid, DownloadAction.Pause, null, deadline.Token).ConfigureAwait(false);
                    }
                }
                while (true)
                {
                    var settled = true;
                    foreach (var gid in inventory)
                    {
                        var row = await rpc.FindAsync(gid, deadline.Token).ConfigureAwait(false) ?? throw LegacyRefused();
                        if (row.Progress.Status is DownloadStatus.Downloading or DownloadStatus.Queued) settled = false;
                        else if (!await MatchesLegacyEntryAsync(rpc, entries[gid], row, deadline.Token).ConfigureAwait(false)) throw LegacyRefused();
                    }
                    if (settled) break;
                    await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                }
                var finalInventory = await rpc.GetInventoryAsync(deadline.Token, 4096).ConfigureAwait(false);
                if (!inventory.ToHashSet(StringComparer.Ordinal).SetEquals(finalInventory)) throw LegacyRefused();
                RequireLegacyProcess(process, owner);
                await rpc.SaveSessionAsync(deadline.Token).ConfigureAwait(false);
                RequireLegacyProcess(process, owner);
                await rpc.ShutdownAsync(deadline.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); // No force-termination fallback.
                _log.Write("migration", "Verified older engine stopped gracefully; retained files and session preserved.");
            }
            finally { _lease?.Dispose(); _lease = null; }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new EngineException(EngineFailure.AlreadyRunning, "Older-engine stop timed out. It was not force terminated; its files and checkpoint were preserved."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or Win32Exception or InvalidOperationException)
        { throw LegacyRefused(); }
        finally { _gate.Release(); }
    }
    private void RequireLegacyProcess(Process process, EngineOwnership owner)
    {
        if (process.HasExited || process.StartTime.ToUniversalTime() != owner.StartedAtUtc
            || !string.Equals(process.MainModule?.FileName, Path.GetFullPath(owner.ExecutablePath), StringComparison.OrdinalIgnoreCase)) throw LegacyRefused();
    }
    private static Task<bool> MatchesLegacyEntryAsync(Aria2RpcClient rpc, DownloadEntry entry, EngineDownload observed, CancellationToken token) =>
        DownloadBrowsing.IsTerminal(observed.Progress.Status)
            ? rpc.MatchesStoppedOwnershipAsync(entry.Attempt.EngineId, entry.ExactSourceUrl ?? entry.Job.Source.AbsoluteUri, entry.Job.Destination, token)
            : rpc.MatchesTransferOwnershipAsync(entry.Attempt.EngineId, entry.ExactSourceUrl ?? entry.Job.Source.AbsoluteUri, entry.Job.Destination, token);
    private static FileStream OpenPrivateMetadata(string path, long limit)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw LegacyRefused();
        var file = new FileInfo(path);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw LegacyRefused();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = file.GetAccessControl();
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        // Elevated Windows processes can create a file owned by their token's
        // default owner (Administrators) even when its ACL grants only this user
        // and SYSTEM. The ACE check below remains authoritative.
        if (owner is null || !owner.Equals(user) && !owner.Equals(system) && !owner.Equals(identity.Owner)) throw LegacyRefused();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(user) && !rule.IdentityReference.Equals(system)) throw LegacyRefused();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) { stream.Dispose(); throw LegacyRefused(); }
        return stream;
    }
    private static EngineException LegacyRefused() => new(EngineFailure.AlreadyRunning,
        "The older engine could not be verified for a safe stop. Its process and files were preserved; no new engine was started.");
}
