using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Tests;

[Collection("Engine processes")]
public sealed class RecoveryTests
{
    [Fact]
    public async Task GUI_crash_adopts_the_verified_survivor_without_resubmitting_or_losing_pause()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        var oldPid = await runtime.StartHost(server.Url, "paused");
        var before = Assert.Single(await runtime.Repository().LoadAsync(default));
        var requests = server.Requests;
        await runtime.KillHost();
        Assert.True(runtime.EngineAlive());
        await using var manager = runtime.Manager(); var info = await manager.StartAsync(default);
        Assert.Equal(oldPid, manager.OwnedProcessId); Assert.Contains("surviving", info.RecoveryMessage);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(manager, store); await coordinator.RefreshAsync();
        var after = Assert.Single(store.Current.Downloads);
        Assert.Equal(before.Attempt, after.Attempt); Assert.Equal(DownloadStatus.Paused, after.Progress.Status); Assert.True(after.DesiredPaused);
        Assert.Equal(requests, server.Requests); using var db = runtime.Db(); Assert.Single(db.Attempts);
    }

    [Fact]
    public async Task Engine_and_GUI_crash_restore_the_checkpoint_with_the_same_gid_and_paused_intent()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await runtime.StartHost(server.Url, "paused");
        var before = Assert.Single(await runtime.Repository().LoadAsync(default));
        await runtime.KillEngine(); await runtime.KillHost();
        await using var manager = runtime.Manager(); var info = await manager.StartAsync(default);
        Assert.Contains("Restored 1", info.RecoveryMessage);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(manager, store); await coordinator.RefreshAsync();
        var after = Assert.Single(store.Current.Downloads);
        Assert.Equal(before.Attempt.EngineId, after.Attempt.EngineId); Assert.Equal(DownloadStatus.Paused, after.Progress.Status);
        Assert.True(File.Exists(before.Job.Destination + ".aria2"));
        await coordinator.ResumeAsync(after.Job.Id); await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Completed);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(before.Job.Destination));
        using var db = runtime.Db(); Assert.Single(db.Attempts);
    }

    [Fact]
    public async Task Crash_after_acceptance_before_database_confirmation_reconciles_the_surviving_gid()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        var pid = await runtime.StartHost(server.Url, "accepted");
        var intent = Assert.Single(await runtime.Repository().LoadAsync(default)); Assert.Equal(OperationStatus.Pending, intent.PendingOperation?.Status);
        await runtime.KillHost(); await using var manager = runtime.Manager(); await manager.StartAsync(default); Assert.Equal(pid, manager.OwnedProcessId);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(manager, store);
        await WaitAsync(coordinator, () => store.Current.Downloads.Count == 1 && store.Current.Downloads[0].Progress.Status == DownloadStatus.Completed);
        Assert.Equal(intent.Attempt.EngineId, store.Current.Downloads[0].Attempt.EngineId); Assert.Null(store.Current.Downloads[0].PendingOperation);
        Assert.Equal(1, server.Requests); using var db = runtime.Db(); Assert.Single(db.Attempts);
    }

    [Fact]
    public async Task Crash_before_rpc_preserves_intent_without_auto_add_then_manual_recovery_uses_same_id()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await runtime.StartHost(server.Url, "before-rpc"); var before = Assert.Single(await runtime.Repository().LoadAsync(default));
        await runtime.KillHost(); await using var manager = runtime.Manager(); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(manager, store); await coordinator.RefreshAsync();
        Assert.Equal(0, server.Requests); Assert.Equal(DownloadStatus.Interrupted, store.Current.Downloads[0].Progress.Status);
        await coordinator.RecoverAsync(before.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Completed);
        Assert.Equal(before.Attempt, store.Current.Downloads[0].Attempt); Assert.Equal(1, server.Requests);
        using var db = runtime.Db(); Assert.Single(db.Attempts); Assert.Equal(2, db.Operations.Count());
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(before.Job.Destination));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("executable")]
    [InlineData("secret")]
    public async Task Failed_adoption_proof_preserves_metadata_and_does_not_stop_the_process(string mismatch)
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await runtime.StartHost(server.Url, "paused"); await runtime.KillHost();
        var path = Path.Combine(runtime.Root, "engine.owner.json");
        var owner = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        if (mismatch == "session") owner["SessionId"] = "a-different-session";
        if (mismatch == "executable") owner["ExecutablePath"] = Path.Combine(runtime.Root, "unrelated.exe");
        await File.WriteAllTextAsync(path, owner.ToJsonString());
        if (mismatch == "secret")
        {
            var config = Path.Combine(runtime.Root, "aria2.conf");
            var lines = await File.ReadAllLinesAsync(config);
            await File.WriteAllLinesAsync(config, lines.Select(line => line.StartsWith("rpc-secret=", StringComparison.Ordinal) ? "rpc-secret=" + new string('0', 64) : line));
        }
        var saved = await File.ReadAllTextAsync(path);
        await using var manager = runtime.Manager(); await Assert.ThrowsAsync<EngineException>(() => manager.StartAsync(default));
        Assert.Null(manager.OwnedProcessId); Assert.True(runtime.EngineAlive());
        Assert.Equal(saved, await File.ReadAllTextAsync(path)); Assert.True(File.Exists(Path.Combine(runtime.Root, "aria2.conf")));
    }

    [Fact]
    public async Task Clean_exit_and_relaunch_restore_paused_history_and_preserve_unknown_session_records()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        DownloadEntry before;
        await using (var first = runtime.Manager())
        {
            await first.StartAsync(default); var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(first, store);
            await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "paused.bin");
            before = store.Current.Downloads[0]; await coordinator.PauseAsync(before.Job.Id); await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Paused);
            await first.StopAsync(default);
        }
        var session = Directory.GetFiles(Path.Combine(runtime.Root, "sessions"), "session-*.txt").Single();
        await File.AppendAllTextAsync(session, "https://example.com/unknown\n gid=abcdef0123456789\n out=unowned.bin\n");
        var preserved = await File.ReadAllTextAsync(session);
        await using var restored = runtime.Manager(); var info = await restored.StartAsync(default);
        Assert.Contains("1 unrecognized", info.RecoveryMessage); Assert.Equal(preserved, await File.ReadAllTextAsync(session));
        var inventory = await restored.ObserveDownloadsAsync([before.Attempt.EngineId], default);
        Assert.Equal(DownloadStatus.Paused, Assert.Single(inventory.Downloads).Progress.Status);
        Assert.False(File.Exists(Path.Combine(runtime.Output, "unowned.bin")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_session_or_control_file_is_reported_without_overwrite_or_false_completion(bool missingControl)
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await runtime.StartHost(server.Url, "paused"); var before = Assert.Single(await runtime.Repository().LoadAsync(default));
        await runtime.KillEngine(); await runtime.KillHost();
        if (missingControl) File.Delete(before.Job.Destination + ".aria2");
        else foreach (var file in Directory.GetFiles(Path.Combine(runtime.Root, "sessions"), "session-*.txt")) File.Delete(file);
        var bytes = await File.ReadAllBytesAsync(before.Job.Destination); var requests = server.Requests;
        await using var manager = runtime.Manager(); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(manager, store); await coordinator.RefreshAsync();
        Assert.Equal(DownloadStatus.Interrupted, store.Current.Downloads[0].Progress.Status); Assert.Equal(requests, server.Requests);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(before.Job.Destination));
        if (missingControl) await Assert.ThrowsAsync<DownloadRequestException>(() => coordinator.RecoverAsync(before.Job.Id));
        else
        {
            await coordinator.RecoverAsync(before.Job.Id); Assert.Equal(DownloadStatus.Paused, store.Current.Downloads[0].Progress.Status);
            await coordinator.ResumeAsync(before.Job.Id); await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Completed);
            Assert.Equal(server.Payload, await File.ReadAllBytesAsync(before.Job.Destination));
        }
    }

    [Fact]
    public async Task Engine_crash_in_live_app_restores_active_attempt_and_uses_a_new_connection_generation()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await using var manager = runtime.Manager(); var first = await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(manager, store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "active.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.DownloadedBytes > 0);
        await manager.CheckpointAsync(default);
        var before = store.Current.Downloads[0];
        using (var process = Process.GetProcessById(manager.OwnedProcessId!.Value)) { process.Kill(); await process.WaitForExitAsync(); }
        var restarted = await manager.StartAsync(default); Assert.NotEqual(first.SessionId, restarted.SessionId);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Completed);
        Assert.Equal(before.Attempt, store.Current.Downloads[0].Attempt); Assert.Equal(server.Payload, await File.ReadAllBytesAsync(before.Job.Destination));
    }

    [Fact]
    public async Task Manual_recovery_applies_an_uncertain_pause_to_an_existing_gid_without_another_add()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await using var manager = runtime.Manager(); await manager.StartAsync(default);
        var client = new UnsubmittedCommand(new ManagedAria2Client(manager)); var store = new DownloadRuntimeStore();
        var coordinator = new DownloadCoordinator(client, store, TimeProvider.System, runtime.Repository(), new FileDownloadDestination());
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "intent.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.DownloadedBytes > 0);
        var original = store.Current.Downloads[0]; var requests = server.Requests;
        await Assert.ThrowsAsync<EngineException>(() => coordinator.PauseAsync(original.Job.Id));
        await coordinator.RefreshAsync(); Assert.Equal(OperationStatus.Uncertain, store.Current.Downloads[0].PendingOperation?.Status);
        Assert.True((await runtime.Repository().LoadAsync(default))[0].DesiredPaused);
        await coordinator.RecoverAsync(original.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Paused);
        Assert.Equal(original.Attempt, store.Current.Downloads[0].Attempt); Assert.Null(store.Current.Downloads[0].PendingOperation);
        Assert.Equal(requests, server.Requests); Assert.Equal(1, client.Adds); using var db = runtime.Db(); Assert.Single(db.Attempts);
        Assert.Equal(OperationStatus.Failed, db.Operations.Single(row => row.Action == DownloadAction.Pause).Status);
    }

    [Fact]
    public async Task Unsubmitted_remove_is_not_restarted_after_engine_crash_and_keeps_partial_files()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await using var manager = runtime.Manager(); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore();
        var coordinator = new DownloadCoordinator(new UnsubmittedCommand(new ManagedAria2Client(manager)), store, TimeProvider.System, runtime.Repository(), new FileDownloadDestination());
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "removed.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.DownloadedBytes > 0);
        var original = store.Current.Downloads[0]; await manager.CheckpointAsync(default);
        await Assert.ThrowsAsync<EngineException>(() => coordinator.RemoveAsync(original.Job.Id));
        using (var process = Process.GetProcessById(manager.OwnedProcessId!.Value)) { process.Kill(); await process.WaitForExitAsync(); }
        var bytes = await ReadAfterExitAsync(original.Job.Destination); var requests = server.Requests;
        await manager.StartAsync(default); await coordinator.RefreshAsync();
        Assert.Equal(DownloadStatus.Removed, store.Current.Downloads[0].Progress.Status); Assert.Null(store.Current.Downloads[0].PendingOperation);
        Assert.Equal(requests, server.Requests); Assert.Equal(bytes, await File.ReadAllBytesAsync(original.Job.Destination));
        Assert.True(File.Exists(original.Job.Destination + ".aria2"));
        Assert.Empty((await manager.ObserveDownloadsAsync([original.Attempt.EngineId], default)).Downloads);
    }

    private sealed class UnsubmittedCommand(IAria2Client inner) : IAria2Client
    {
        public int Adds;
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token) { Adds++; return inner.AddAsync(request, token); }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => inner.GetDownloadsAsync(ids, token);
        public Task<EngineObservation> ObserveAsync(IReadOnlyList<string> ids, CancellationToken token) => inner.ObserveAsync(ids, token);
        public Task<EngineDownload> RecoverAsync(DownloadEntry entry, CancellationToken token) => inner.RecoverAsync(entry, token);
        public Task<EngineCommandResult> ExecuteAsync(string gid, DownloadAction action, int? position, CancellationToken token) =>
            throw new EngineException(EngineFailure.RpcUnavailable, "Command was never submitted.");
    }

    private static async Task<byte[]> ReadAfterExitAsync(string path)
    {
        // Kernel cleanup of a forcibly terminated process can briefly lag its exit signal.
        for (var attempt = 0; ; attempt++)
        {
            try { return await File.ReadAllBytesAsync(path); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < 10) { await Task.Delay(100); }
        }
    }

    private static async Task WaitAsync(DownloadCoordinator coordinator, Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30)) { await coordinator.RefreshAsync(); if (done()) return; await Task.Delay(100); }
        Assert.Fail("Recovery state was not observed within 30 seconds.");
    }
    private sealed class Runtime : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.RecoveryTests", Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "downloads");
        private string Database => Path.Combine(Root, "downloads.db");
        public Aria2ProcessOptions Options { get; }
        private Process? _host;
        private int? _enginePid;
        private DateTime _engineStart;
        public Runtime()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Options = new(Path.Combine(AppContext.BaseDirectory, "Engine", "aria2c.exe"), Root) { RpcPort = port };
        }
        public SqliteDownloadRepository Repository() => new(Database);
        public DownloadDbContext Db() => new(new DbContextOptionsBuilder<DownloadDbContext>().UseSqlite($"Data Source={Database};Pooling=False").Options);
        public Aria2ProcessManager Manager() => new(Options, Repository());
        public DownloadCoordinator Coordinator(Aria2ProcessManager manager, DownloadRuntimeStore store) => new(new ManagedAria2Client(manager), store, TimeProvider.System, Repository(), new FileDownloadDestination());
        public async Task<int> StartHost(string url, string mode)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AriaGui.EngineFixture.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "--recovery-host", Root, Options.ExecutablePath, Options.RpcPort.ToString(), new Uri(new Uri(url), "slow").AbsoluteUri, mode }) start.ArgumentList.Add(value);
            _host = Process.Start(start)!;
            _ = _host.StandardError.ReadToEndAsync(); // Drain errors so a failed host cannot fill the pipe and hide its exit.
            var line = await _host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(line); Assert.StartsWith("READY ", line);
            _enginePid = int.Parse(line[6..]); using var engine = Process.GetProcessById(_enginePid.Value); _engineStart = engine.StartTime.ToUniversalTime();
            return _enginePid.Value;
        }
        public async Task KillHost() { if (_host is { HasExited: false }) { _host.Kill(); await _host.WaitForExitAsync(); } }
        public bool EngineAlive()
        {
            if (_enginePid is null) return false;
            try { using var process = Process.GetProcessById(_enginePid.Value); return !process.HasExited && process.StartTime.ToUniversalTime() == _engineStart; }
            catch (ArgumentException) { return false; }
        }
        public async Task KillEngine()
        {
            if (!EngineAlive()) return;
            using var process = Process.GetProcessById(_enginePid!.Value);
            if (!string.Equals(process.MainModule?.FileName, Options.ExecutablePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test engine identity.");
            process.Kill(); await process.WaitForExitAsync();
        }
        public void Dispose()
        {
            KillHost().GetAwaiter().GetResult(); _host?.Dispose();
            var ownerFile = Path.Combine(Root, "engine.owner.json");
            if (_enginePid is null && File.Exists(ownerFile))
            {
                var owner = JsonNode.Parse(File.ReadAllText(ownerFile))!;
                if (string.Equals(owner["ExecutablePath"]?.GetValue<string>(), Options.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                { _enginePid = owner["ProcessId"]!.GetValue<int>(); _engineStart = owner["StartedAtUtc"]!.GetValue<DateTime>(); }
            }
            KillEngine().GetAwaiter().GetResult();
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.RecoveryTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
