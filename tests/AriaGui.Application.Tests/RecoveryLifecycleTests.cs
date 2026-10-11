using AriaGui.Application.Engine;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Tests;

public sealed class RecoveryLifecycleTests
{
    [Fact]
    public async Task Close_hides_without_stopping_and_explicit_exit_is_deduplicated()
    {
        var host = new DesktopHost(); var desktop = new DesktopLifetimeController(host);
        Assert.True(desktop.CloseToTray()); Assert.Equal(1, host.Hides); Assert.Equal(0, host.Exits);
        desktop.Open(); Assert.Equal(1, host.Shows);
        var first = desktop.ExitAsync(); var second = desktop.ExitAsync();
        Assert.Same(first, second); Assert.Equal(1, host.Exits);
        Assert.False(desktop.CloseToTray()); desktop.Open(); Assert.Equal(1, host.Shows);
        host.Done.SetResult(); await first;
    }
    [Fact]
    public void Unavailable_tray_keeps_the_window_reachable()
    {
        var host = new DesktopHost { Tray = false }; var desktop = new DesktopLifetimeController(host);
        Assert.False(desktop.CloseToTray()); Assert.Equal(0, host.Hides); Assert.Equal(1, host.Shows); Assert.Equal(0, host.Exits);
    }
    [Fact]
    public async Task Automatic_restarts_back_off_and_stop_after_three_retries_manual_start_resets_budget()
    {
        var clock = new Clock(); var engine = new EngineStub { Failure = EngineFailure.RpcUnavailable }; var store = new EngineStateStore();
        using var service = new EngineLifecycleService(engine, store, clock);
        await service.StartAsync();
        await service.MonitorOnceAsync(); Assert.Equal(1, engine.Starts);
        clock.Advance(2); await service.MonitorOnceAsync(); Assert.Equal(2, engine.Starts);
        await service.MonitorOnceAsync(); Assert.Equal(2, engine.Starts);
        clock.Advance(4); await service.MonitorOnceAsync(); Assert.Equal(3, engine.Starts);
        clock.Advance(8); await service.MonitorOnceAsync(); Assert.Equal(4, engine.Starts);
        clock.Advance(100); await service.MonitorOnceAsync(); Assert.Equal(4, engine.Starts);
        Assert.Contains("Automatic recovery stopped", store.Current.Message);
        engine.Failure = null; await service.StartAsync(); Assert.Equal(EngineStatus.Ready, store.Current.Status); Assert.Equal(5, engine.Starts);
    }
    [Theory]
    [InlineData(EngineFailure.AlreadyRunning)]
    [InlineData(EngineFailure.Authentication)]
    [InlineData(EngineFailure.InvalidBinary)]
    [InlineData(EngineFailure.PortInUse)]
    public async Task Ownership_security_and_port_failures_are_not_automatically_retried(EngineFailure failure)
    {
        var engine = new EngineStub { Failure = failure }; var store = new EngineStateStore();
        using var service = new EngineLifecycleService(engine, store);
        await service.StartAsync(); for (var i = 0; i < 8; i++) await service.MonitorOnceAsync();
        Assert.Equal(1, engine.Starts); Assert.Equal(failure, store.Current.Failure);
    }
    [Fact]
    public async Task Suspend_checkpoints_and_resume_verifies_the_same_live_engine()
    {
        var engine = new EngineStub(); var store = new EngineStateStore(); using var service = new EngineLifecycleService(engine, store);
        await service.StartAsync(); await service.OnSuspendAsync(); await service.OnResumeAsync();
        Assert.Equal(1, engine.Starts); Assert.Equal(1, engine.Checkpoints); Assert.Equal(1, engine.Checks); Assert.Equal(EngineStatus.Ready, store.Current.Status);
    }
    [Fact]
    public async Task Missing_add_intent_waits_for_explicit_recovery_and_retains_the_original_attempt_id()
    {
        var repository = new MemoryDownloadRepository(); var client = new RecoveryClient(); var store = new DownloadRuntimeStore();
        var coordinator = new DownloadCoordinator(client, store, TimeProvider.System, repository, new TestDownloadDestination());
        await Assert.ThrowsAsync<EngineException>(() => coordinator.AddAsync("https://example.com/file"));
        var original = store.Current.Downloads[0];
        await coordinator.RefreshAsync(); Assert.Equal(DownloadStatus.Interrupted, store.Current.Downloads[0].Progress.Status);
        Assert.Equal(1, client.Adds); Assert.Equal(0, client.Recovers);
        await coordinator.RecoverAsync(original.Job.Id);
        var restored = Assert.Single(store.Current.Downloads); Assert.Equal(original.Attempt, restored.Attempt);
        Assert.Equal(DownloadStatus.Paused, restored.Progress.Status); Assert.Null(restored.PendingOperation);
        Assert.Equal(DownloadAction.Recover, repository.Intents.Last().PendingOperation?.Action); Assert.Equal(1, client.Recovers);
    }
    [Fact]
    public async Task Unknown_engine_entries_are_reported_without_becoming_jobs_or_receiving_commands()
    {
        var client = new RecoveryClient { Unknown = 4 }; var store = new DownloadRuntimeStore();
        var coordinator = new DownloadCoordinator(client, store, TimeProvider.System, new MemoryDownloadRepository(), new TestDownloadDestination());
        await coordinator.RefreshAsync(); Assert.Empty(store.Current.Downloads); Assert.Contains("4 unrecognized", store.Current.ErrorMessage);
        Assert.Equal(0, client.Adds); Assert.Equal(0, client.Recovers);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
    private sealed class DesktopHost : IDesktopLifetimeHost
    {
        public bool Tray = true; public int Hides, Shows, Exits;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool EnsureTray() => Tray; public void Hide() => Hides++; public void Show() => Shows++;
        public Task ExitAsync() { Exits++; return Done.Task; }
    }
    private sealed class EngineStub : IManagedAria2Engine
    {
        public event EventHandler? UnexpectedExit { add { } remove { } }
        public EngineFailure? Failure; public int Starts, Checks, Checkpoints;
        public Task<EngineInfo> StartAsync(CancellationToken token)
        { Starts++; if (Failure is { } failure) throw new EngineException(failure, "Injected failure"); return Task.FromResult(new EngineInfo("1.37.0", "same-session")); }
        public Task<EngineInfo> CheckHealthAsync(CancellationToken token) { Checks++; return Task.FromResult(new EngineInfo("1.37.0", "same-session")); }
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public Task CheckpointAsync(CancellationToken token) { Checkpoints++; return Task.CompletedTask; }
    }
    private sealed class RecoveryClient : IAria2Client
    {
        public int Adds, Recovers, Unknown;
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        { Adds++; throw new EngineException(EngineFailure.RpcUnavailable, "Uncertain submission"); }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.FromResult<IReadOnlyList<EngineDownload>>([]);
        public Task<EngineObservation> ObserveAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.FromResult(new EngineObservation("fresh", [], 3, UnknownTransferCount: Unknown));
        public Task<EngineDownload> RecoverAsync(DownloadEntry entry, CancellationToken token)
        { Recovers++; return Task.FromResult(new EngineDownload(entry.Attempt.EngineId, new(DownloadStatus.Paused, 0, null, 0))); }
    }
}
