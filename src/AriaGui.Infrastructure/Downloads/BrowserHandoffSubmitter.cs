using System.Collections.Concurrent;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;

namespace AriaGui.Infrastructure.Downloads;

public sealed class BrowserHandoffSubmitter : IBrowserHandoffSubmitter, IDisposable
{
    private sealed record Ownership(string Folder, string Path, Uri Relay);
    private readonly ConcurrentDictionary<Guid, Ownership> _owned = new();
    private readonly DownloadCoordinator coordinator;
    private readonly DownloadPreferencesService preferences;
    private readonly BrowserCookieRelay relay;
    private readonly DownloadRuntimeStore store;

    public BrowserHandoffSubmitter(DownloadCoordinator coordinator, DownloadPreferencesService preferences,
        BrowserCookieRelay relay, DownloadRuntimeStore store)
    {
        this.coordinator = coordinator; this.preferences = preferences; this.relay = relay; this.store = store;
        store.Changed += DownloadsChanged;
    }

    private void DownloadsChanged(object? sender, DownloadRuntimeSnapshot snapshot)
    {
        foreach (var entry in snapshot.Downloads.Where(item => item.Progress.Status is
            DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Removed))
        {
            if (!_owned.TryGetValue(entry.Job.Id, out var owned)) continue;
            relay.Revoke(owned.Relay);
            if (entry.Progress.Status == DownloadStatus.Completed)
                _owned.TryRemove(entry.Job.Id, out _);
        }
    }

    public void Dispose() => store.Changed -= DownloadsChanged;

    public async Task<BrowserHandoffSubmission> SubmitAsync(BrowserHandoffRequest request, bool startPaused, CancellationToken token)
    {
        var folder = await preferences.ResolveDefaultFolderAsync(token).ConfigureAwait(false);
        var name = request.Filename ?? FileDownloadDestination.SuggestedName(request.Url);
        if (!FileDownloadDestination.IsValidName(name)) name = "download.bin";
        var address = await relay.RegisterAsync(request, token).ConfigureAwait(false);
        try
        {
            var entry = await coordinator.AddBrowserRelayAsync(address, folder, name, startPaused, token).ConfigureAwait(false);
            _owned[entry.Job.Id] = new(folder, entry.Job.Destination, address);
            DownloadsChanged(this, store.Current);
            return new(entry.Job.Id, entry.Job.DisplayName, startPaused);
        }
        catch
        {
            // Keep the route if aria2's add result is uncertain; a transfer may still need it.
            throw;
        }
    }

    public async Task<bool> UndoAsync(Guid id, CancellationToken token)
    {
        if (!_owned.TryGetValue(id, out var owned)) return false;
        var entry = await coordinator.GetBrowserEntryAsync(id, token).ConfigureAwait(false);
        if (entry is null || entry.Progress.Status == DownloadStatus.Completed) return false;
        if (!InsideFolder(owned.Folder, owned.Path) || !string.Equals(entry.Job.Destination, owned.Path, StringComparison.OrdinalIgnoreCase))
            return false;
        if (entry.Progress.Status != DownloadStatus.Removed)
        {
            try { await coordinator.RemoveAsync(id, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
            catch (Exception ex) when (ex is IOException or DownloadRequestException or EngineException) { return false; }
        }
        entry = await coordinator.GetBrowserEntryAsync(id, token).ConfigureAwait(false);
        if (entry?.Progress.Status != DownloadStatus.Removed) return false;
        try
        {
            foreach (var path in new[] { owned.Path, owned.Path + ".aria2" })
            {
                if (!InsideFolder(owned.Folder, path)) return false;
                if (!File.Exists(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                File.Delete(path);
            }
            if (!DeleteOwnedStage(owned.Folder, id)) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        relay.Revoke(owned.Relay);
        _owned.TryRemove(id, out _);
        return true;
    }

    private static bool InsideFolder(string folder, string path)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(candidate)?.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static bool DeleteOwnedStage(string folder, Guid id)
    {
        var root = Path.Combine(folder, ".aria-gui-continuity-" + id.ToString("N"));
        if (!InsideFolder(folder, root)) return false;
        if (!Directory.Exists(root)) return true;
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!Path.GetFullPath(child).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) return false;
                if (Directory.Exists(child)) pending.Push(child);
                else files.Add(child);
            }
        }
        foreach (var file in files) File.Delete(file);
        return true;
    }
}
