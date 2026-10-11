using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Browser;

namespace AriaGui.Infrastructure.Tests;

public sealed class BrowserBridgeProcessTests
{
    [Fact]
    public void ReleasedPrimaryLeaseCannotAdvertiseOwnership()
    {
        using var fixture = new Fixture(); using var lease = new AriaGui.Infrastructure.Aria2.SingleInstanceLease(fixture.Root);
        Assert.True(lease.IsPrimary); lease.Dispose(); Assert.False(lease.IsPrimary);
    }
    [Fact]
    public async Task W02TenSimultaneousSyntheticLaunchesSelectOnePrimaryListener()
    {
        using var fixture = new Fixture(); var hosts = Enumerable.Range(0, 10).Select(_ => fixture.Host()).ToArray();
        var stages = await Task.WhenAll(hosts.Select(fixture.LineAsync));
        Assert.Equal(1, stages.Count(stage => stage == "STARTING")); Assert.Equal(9, stages.Count(stage => stage == "SECONDARY"));
        var primary = hosts[Array.IndexOf(stages, "STARTING")];
        await primary.StandardInput.WriteLineAsync("START"); var endpoint = JsonSerializer.Deserialize<Endpoint>(await fixture.LineAsync(primary))!;
        Assert.Equal(ResultCode.Ready, (await fixture.CallAsync(endpoint, Hello(endpoint))).Result.Code);
        foreach (var secondary in hosts.Where(host => host != primary))
        { await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, secondary.ExitCode); }
        await fixture.ExitAsync(primary);
    }
    [Fact]
    public async Task DisconnectedClientDoesNotOwnDurableIntakeLifetime()
    {
        using var fixture = new Fixture(); var host = fixture.Host(); var endpoint = await fixture.StartAsync(host);
        var id = Guid.NewGuid(); var request = new RequestEnvelope(new(1, 0), id, BrowserAction.AddPublicGet,
            new PublicGetPayload(endpoint.InstallationId, DateTimeOffset.UtcNow, "https://example.invalid/drop", "drop.zip"));
        await fixture.DropAsync(endpoint, request);
        var statusRequest = new RequestEnvelope(new(1, 0), Guid.NewGuid(), BrowserAction.GetRequestStatus, new StatusPayload(endpoint.InstallationId, id));
        ResponseEnvelope? status = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            status = await fixture.CallAsync(endpoint, statusRequest);
            if (status.Result.Code == ResultCode.Accepted) break;
            await Task.Delay(50);
        }
        Assert.Equal(ResultCode.Accepted, status!.Result.Code); Assert.Equal("STATS 0 1", await fixture.CommandAsync(host, "STATS"));
        await fixture.ExitAsync(host);
    }
    [Fact]
    public async Task W01W02TenProcessesConvergeOfflineHiddenSimulationAndShutdownAdmissionStops()
    {
        using var fixture = new Fixture(); var host = fixture.Host(); var endpoint = await fixture.StartAsync(host);
        var id = Guid.NewGuid(); var add = new RequestEnvelope(new(1, 0), id, BrowserAction.AddPublicGet,
            new PublicGetPayload(endpoint.InstallationId, DateTimeOffset.UtcNow, "https://example.invalid/file?sig=%41%7e%2f", "fixture.zip"));
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => fixture.CallAsync(endpoint, add)));
        Assert.All(responses, response => Assert.Equal(ResultCode.Accepted, response.Result.Code));
        Assert.Single(responses.Select(response => response.Result.JobId).Distinct()); Assert.All(responses, response => Assert.Equal(JobIntakeState.Queued, response.Result.State));
        Assert.Equal("STATS 0 1", await fixture.CommandAsync(host, "STATS"));
        Assert.Equal("HIDDEN_SIMULATION", await fixture.CommandAsync(host, "TRAY"));
        var status = await fixture.CallAsync(endpoint, new(new(1, 0), Guid.NewGuid(), BrowserAction.GetRequestStatus, new StatusPayload(endpoint.InstallationId, id)));
        Assert.Equal(responses[0].Result.JobId, status.Result.JobId);
        var secondary = fixture.Host(); Assert.Equal("SECONDARY", await fixture.LineAsync(secondary)); await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, secondary.ExitCode);
        Assert.Equal("BARRIER", await fixture.CommandAsync(host, "STOP"));
        Assert.Null(await fixture.TryCallAsync(endpoint, new(new(1, 0), Guid.NewGuid(), BrowserAction.Hello, new HelloPayload(endpoint.InstallationId, "1.0.0"))));
        await fixture.ExitAsync(host);
        var restarted = fixture.Host(); var again = await fixture.StartAsync(restarted); Assert.Equal(endpoint.InstallationId, again.InstallationId); Assert.Equal(endpoint.PipeName, again.PipeName);
        var recovered = await fixture.CallAsync(again, new(new(1, 0), Guid.NewGuid(), BrowserAction.GetRequestStatus, new StatusPayload(again.InstallationId, id)));
        Assert.Equal(status.Result.JobId, recovered.Result.JobId); Assert.Equal(JobIntakeState.Queued, recovered.Result.State); Assert.Equal("STATS 0 1", await fixture.CommandAsync(restarted, "STATS")); await fixture.ExitAsync(restarted);
    }
    [Fact]
    public async Task StartupDoesNotAdvertiseListenerUntilStorageAndPreferencesReady()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var fixture = new Fixture(); var host = fixture.Host(); Assert.Equal("STARTING", await fixture.LineAsync(host));
        var installation = Guid.Parse(File.ReadAllText(Path.Combine(fixture.Root, "browser-installation-v1.txt")));
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var name = BrowserBridgeEndpoint.Name(fixture.Root, "fixture", "unpackaged", sid, installation);
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipe.ConnectAsync(timeout.Token));
        await host.StandardInput.WriteLineAsync("START"); var endpoint = JsonSerializer.Deserialize<Endpoint>(await fixture.LineAsync(host))!;
        Assert.Equal(ResultCode.Ready, (await fixture.CallAsync(endpoint, Hello(endpoint))).Result.Code); await fixture.ExitAsync(host);
    }
    [Fact]
    public async Task W03W04WrongInstallationAndExecutableFailClosed()
    {
        using var fixture = new Fixture(); var host = fixture.Host(); var endpoint = await fixture.StartAsync(host);
        var wrong = await fixture.CallAsync(endpoint, new(new(1, 0), Guid.NewGuid(), BrowserAction.Hello, new HelloPayload(Guid.NewGuid(), "1.0.0")));
        Assert.Equal(ResultCode.Rejected, wrong.Result.Code); Assert.Equal(RejectionReason.InvalidRequest, wrong.Result.Reason);
        using var client = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Impersonation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await client.ConnectAsync(timeout.Token);
        var frames = new FrameConnection(client, FrameTransport.PipeLittleEndian);
        try { await frames.WriteAsync(ContractJson.Serialize(Hello(endpoint)), timeout.Token); Assert.Null(await frames.ReadAsync(timeout.Token)); }
        catch (IOException) { /* Denied peer is disconnected, no reply. */ }
        Assert.Equal("STATS 0 0", await fixture.CommandAsync(host, "STATS")); await fixture.ExitAsync(host);
    }
    [Fact]
    public async Task W04ExistingPipeNameMakesFirstInstanceFailClosed()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var fixture = new Fixture(); var host = fixture.Host(); Assert.Equal("STARTING", await fixture.LineAsync(host));
        var installation = Guid.Parse(File.ReadAllText(Path.Combine(fixture.Root, "browser-installation-v1.txt")));
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        using var squat = new NamedPipeServerStream(BrowserBridgeEndpoint.Name(fixture.Root, "fixture", "unpackaged", sid, installation), PipeDirection.InOut);
        await host.StandardInput.WriteLineAsync("START"); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(9, host.ExitCode);
        Assert.Contains("bridge_fixture_error:", await host.StandardError.ReadToEndAsync());
    }
    [Fact]
    public async Task W05CorruptPreferencesDoesNotProduceReadyOrAccepted()
    {
        using var fixture = new Fixture(); File.WriteAllText(Path.Combine(fixture.Root, "preferences.json"), "broken");
        var host = fixture.Host(); Assert.Equal("STARTING", await fixture.LineAsync(host)); await host.StandardInput.WriteLineAsync("START");
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(9, host.ExitCode);
        Assert.Null(await host.StandardOutput.ReadLineAsync());
    }
    [Fact]
    public void DomainMappingAlwaysFitsClosedWireSchemaAndEndpointFactorsAreIsolated()
    {
        foreach (var status in Enum.GetValues<DownloadStatus>())
        {
            var result = BrowserBridgeServer.Map(new(ResultCode.Accepted, Guid.NewGuid(), status, EngineUncertain: false));
            Assert.True(ContractJson.ParseResponse(ContractJson.Serialize(new ResponseEnvelope(new(1, 0), Guid.NewGuid(), result))).Success);
        }
        Assert.Equal(JobIntakeState.EngineUncertain, BrowserBridgeServer.Map(new(ResultCode.Accepted, Guid.NewGuid(), Reason: RejectionReason.StorageUnavailable, EngineUncertain: true)).State);
        Assert.Equal(JobIntakeState.Removed, BrowserBridgeServer.Map(new(ResultCode.Accepted, Guid.NewGuid(), DownloadStatus.Removed)).State);
        var id = Guid.NewGuid(); var root = Path.GetFullPath(Path.GetTempPath());
        var names = new[] { BrowserBridgeEndpoint.Name(root,"release","unpackaged","sid",id), BrowserBridgeEndpoint.Name(root,"debug","unpackaged","sid",id),
            BrowserBridgeEndpoint.Name(root,"release","packaged","sid",id), BrowserBridgeEndpoint.Name(root,"release","unpackaged","other",id),
            BrowserBridgeEndpoint.Name(root,"release","unpackaged","sid",Guid.NewGuid()), BrowserBridgeEndpoint.Name(Path.Combine(root,"other"),"release","unpackaged","sid",id) };
        Assert.Equal(names.Length, names.Distinct().Count());
    }
    private static RequestEnvelope Hello(Endpoint endpoint) => new(new(1, 0), Guid.NewGuid(), BrowserAction.Hello, new HelloPayload(endpoint.InstallationId, "1.0.0"));
    private sealed record Endpoint(Guid InstallationId, string PipeName, int Pid);
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.BridgeTests", Guid.NewGuid().ToString("N"));
        private readonly List<Process> _children = [];
        private readonly object _childrenLock = new();
        private static string Executable => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AriaGui.EngineFixture.exe");
        internal Fixture() { Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root,"synthetic-browser-bridge"), "synthetic"); }
        private Process Spawn(params string[] args)
        {
            var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            var process = Process.Start(start)!; lock (_childrenLock) _children.Add(process); return process;
        }
        internal Process Host() => Spawn("--bridge-host", Root);
        internal async Task<string> LineAsync(Process process) => (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8))) ?? throw new IOException("Fixture exited before expected stage.");
        internal async Task<Endpoint> StartAsync(Process process) { Assert.Equal("STARTING", await LineAsync(process)); await process.StandardInput.WriteLineAsync("START"); return JsonSerializer.Deserialize<Endpoint>(await LineAsync(process))!; }
        internal async Task<string> CommandAsync(Process process, string command) { await process.StandardInput.WriteLineAsync(command); return await LineAsync(process); }
        internal async Task<ResponseEnvelope?> TryCallAsync(Endpoint endpoint, RequestEnvelope request)
        {
            var child = Spawn("--bridge-client", endpoint.PipeName, endpoint.Pid.ToString());
            await child.StandardInput.WriteLineAsync(Encoding.UTF8.GetString(ContractJson.Serialize(request))); child.StandardInput.Close();
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); await error;
            if (child.ExitCode != 0) return null;
            return ContractJson.ParseResponse(Encoding.UTF8.GetBytes((await output).Trim())).Value;
        }
        internal async Task DropAsync(Endpoint endpoint, RequestEnvelope request)
        {
            var child = Spawn("--bridge-client-drop", endpoint.PipeName, endpoint.Pid.ToString());
            await child.StandardInput.WriteLineAsync(Encoding.UTF8.GetString(ContractJson.Serialize(request))); child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, child.ExitCode);
        }
        internal async Task<ResponseEnvelope> CallAsync(Endpoint endpoint, RequestEnvelope request) => await TryCallAsync(endpoint, request) ?? throw new IOException("Fixture request failed.");
        internal async Task ExitAsync(Process process) { await process.StandardInput.WriteLineAsync("EXIT"); Assert.Equal("CLOSED", await LineAsync(process)); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, process.ExitCode); }
        public void Dispose()
        {
            foreach (var child in _children) { try { if (!child.HasExited) { child.Kill(true); child.WaitForExit(3000); } } catch (InvalidOperationException) { } child.Dispose(); }
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.BridgeTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(); Directory.Delete(Root, true);
        }
    }
}
