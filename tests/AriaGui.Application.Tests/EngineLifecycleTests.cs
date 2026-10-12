using AriaGui.App.Services;
using AriaGui.App.ViewModels;
using AriaGui.Application.Engine;

namespace AriaGui.Application.Tests;

public sealed class EngineLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_transition_runs_only_as_explicit_action_and_failure_never_auto_retries(bool fail)
    {
        var engine = new StubEngine { FailStartup = true, StartupFailure = EngineFailure.AlreadyRunning, SupportsLegacyTransition = true, FailLegacyStop = fail };
        var store = new EngineStateStore(); using var service = new EngineLifecycleService(engine, store);
        using var viewModel = new EngineViewModel(service, store, new ImmediateDispatcher());
        Assert.False(viewModel.StopLegacyCommand.CanExecute(null));
        await service.StartAsync(); Assert.True(viewModel.StopLegacyCommand.CanExecute(null));
        await service.MonitorOnceAsync(); Assert.Equal(0, engine.LegacyStops); Assert.Equal(1, engine.Starts);
        await viewModel.StopLegacyCommand.ExecuteAsync(null); Assert.Equal(1, engine.LegacyStops);
        Assert.Equal(fail ? EngineStatus.Failed : EngineStatus.Stopped, store.Current.Status);
        if (fail) Assert.Equal(EngineFailure.AlreadyRunning, store.Current.Failure);
        await service.MonitorOnceAsync(); Assert.Equal(1, engine.Starts); Assert.Equal(1, engine.LegacyStops);
        Assert.Equal(fail, viewModel.StopLegacyCommand.CanExecute(null));
    }
    [Fact]
    public async Task Startup_publishes_starting_before_ready_and_stop_finishes_stopped()
    {
        var store = new EngineStateStore();
        var transitions = new List<EngineStatus>();
        store.Changed += (_, state) => transitions.Add(state.Status);
        using var service = new EngineLifecycleService(new StubEngine(), store);
        await service.StartAsync();
        Assert.Equal(new[] { EngineStatus.Starting, EngineStatus.Ready }, transitions);
        Assert.Equal("1.37.0", store.Current.Version);
        await service.StopAsync();
        Assert.Equal(EngineStatus.Stopped, store.Current.Status);
    }

    [Fact]
    public async Task Failure_is_visible_and_start_command_retries_successfully()
    {
        var engine = new StubEngine { FailStartup = true };
        var store = new EngineStateStore();
        using var service = new EngineLifecycleService(engine, store);
        using var viewModel = new EngineViewModel(service, store, new ImmediateDispatcher());
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal("Failed", viewModel.StatusLabel);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.CheckCommand.CanExecute(null));
        engine.FailStartup = false;
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsReady);
        Assert.Equal("aria2 1.37.0", viewModel.Version);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.True(viewModel.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Process_crash_updates_the_application_and_disposed_viewmodel_stops_observing()
    {
        var engine = new StubEngine();
        var store = new EngineStateStore();
        using var service = new EngineLifecycleService(engine, store);
        using var viewModel = new EngineViewModel(service, store, new ImmediateDispatcher());
        await service.StartAsync();
        engine.Crash();
        Assert.False(viewModel.IsReady);
        Assert.Equal(EngineFailure.ProcessExited, store.Current.Failure);
        viewModel.Dispose();
        await service.StartAsync();
        Assert.Equal("Failed", viewModel.StatusLabel);
    }

    [Fact]
    public async Task Crash_during_handshake_is_not_overwritten_with_ready()
    {
        var engine = new StubEngine { CrashDuringStart = true };
        var store = new EngineStateStore();
        using var service = new EngineLifecycleService(engine, store);
        await service.StartAsync();
        Assert.Equal(EngineStatus.Failed, store.Current.Status);
    }

    [Fact]
    public async Task Shutdown_cancellation_does_not_turn_startup_into_a_failure()
    {
        var store = new EngineStateStore();
        using var service = new EngineLifecycleService(new StubEngine { WaitForCancellation = true }, store);
        using var cancellation = new CancellationTokenSource();
        var start = service.StartAsync(cancellation.Token);
        Assert.Equal(EngineStatus.Starting, store.Current.Status);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(EngineStatus.Stopped, store.Current.Status);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher { public void Enqueue(Action action) => action(); }

    private sealed class StubEngine : IManagedAria2Engine
    {
        public bool SupportsLegacyTransition { get; set; }
        public EngineFailure StartupFailure { get; set; } = EngineFailure.MissingBinary;
        public bool FailLegacyStop { get; set; }
        public int Starts, LegacyStops;
        public Task StopLegacyDirectAsync(CancellationToken token)
        {
            LegacyStops++;
            if (FailLegacyStop) throw new EngineException(EngineFailure.RpcUnavailable, "Stop unavailable");
            return Task.CompletedTask;
        }
        public event EventHandler? UnexpectedExit;
        public bool FailStartup { get; set; }
        public bool CrashDuringStart { get; init; }
        public bool WaitForCancellation { get; init; }
        public void Crash() => UnexpectedExit?.Invoke(this, EventArgs.Empty);
        public async Task<EngineInfo> StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (FailStartup) throw new EngineException(StartupFailure, "Engine startup refused.");
            if (CrashDuringStart) Crash();
            return new("1.37.0", "test-session");
        }
        public Task<EngineInfo> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new EngineInfo("1.37.0", "test-session"));
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
