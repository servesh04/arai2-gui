namespace AriaGui.Application.Engine;

public sealed class EngineLifecycleService : IDisposable
{
    private readonly IManagedAria2Engine _engine;
    private readonly EngineStateStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private int _restartAttempts;
    private DateTimeOffset _nextRestart;
    private DateTimeOffset? _readySince;
    private int _monitoring;
    private readonly SemaphoreSlim _monitorGate = new(1, 1);
    private readonly object _restartSync = new();
    private Task<bool>? _restartOperation;

    public EngineLifecycleService(IManagedAria2Engine engine, EngineStateStore store, TimeProvider? clock = null)
    {
        _engine = engine;
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _engine.UnexpectedExit += OnUnexpectedExit;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => StartCoreAsync(true, cancellationToken);
    public Task<bool> RestartAsync(CancellationToken cancellationToken = default)
    {
        lock (_restartSync)
        {
            if (_restartOperation is { IsCompleted: false }) return _restartOperation;
            return _restartOperation = RestartCoreAsync(cancellationToken);
        }
    }

    private async Task<bool> RestartCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _restartAttempts = 0; _nextRestart = default; _readySince = null;
            _store.Publish(EngineStatus.Stopping, "Restarting aria2…");
            try { await _engine.StopAsync(cancellationToken).ConfigureAwait(false); }
            catch (EngineException) { /* StartAsync can recover an already-exited owned process. */ }
            _store.Publish(EngineStatus.Starting, "Reconnecting to aria2…");
            var info = await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
            _readySince = _clock.GetUtcNow();
            _store.Publish(EngineStatus.Ready, info.RecoveryMessage ?? "Ready · local connection verified", info.Version);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _store.Publish(EngineStatus.Stopped, "Engine restart cancelled");
            throw;
        }
        catch (EngineException ex)
        {
            _store.Publish(EngineStatus.Failed, ex.Message, failure: ex.Failure);
            return false;
        }
        catch (Exception)
        {
            _store.Publish(EngineStatus.Failed, "The engine could not restart. Check the diagnostic log.");
            return false;
        }
        finally { _gate.Release(); }
    }
    public bool SupportsLegacyTransition => _engine.SupportsLegacyTransition;
    public async Task StopLegacyDirectAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!SupportsLegacyTransition || _store.Current.Status != EngineStatus.Failed || _store.Current.Failure != EngineFailure.AlreadyRunning) return;
            _store.Publish(EngineStatus.Stopping, "Verifying and gracefully stopping the older engine…");
            try
            {
                await _engine.StopLegacyDirectAsync(token).ConfigureAwait(false);
                _store.Publish(EngineStatus.Stopped, "Older engine stopped; files and session retained. Start the engine, then use Restart fresh for interrupted legacy transfers.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _store.Publish(EngineStatus.Failed, "Older-engine stop cancelled; no termination was forced. Check its state before retrying.", failure: EngineFailure.AlreadyRunning);
                throw;
            }
            catch (EngineException ex) { _store.Publish(EngineStatus.Failed, ex.Message, failure: EngineFailure.AlreadyRunning); }
            catch (Exception) { _store.Publish(EngineStatus.Failed, "Older-engine stop could not finish. Its process and files were preserved.", failure: EngineFailure.AlreadyRunning); }
        }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync(bool manual, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store.Current.Status == EngineStatus.Ready) return;
            if (manual) { _restartAttempts = 0; _nextRestart = default; }
            _store.Publish(EngineStatus.Starting, manual ? "Starting or reconnecting to aria2…" : $"Recovering engine · attempt {_restartAttempts} of 3…");
            try
            {
                var info = await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
                if (_store.Current.Status == EngineStatus.Starting)
                {
                    _readySince = _clock.GetUtcNow();
                    _store.Publish(EngineStatus.Ready, info.RecoveryMessage ?? "Ready · local connection verified", info.Version);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _store.Publish(EngineStatus.Stopped, "Engine startup cancelled");
                throw;
            }
            catch (EngineException ex) { _store.Publish(EngineStatus.Failed, ex.Message, failure: ex.Failure); }
            catch (Exception) { _store.Publish(EngineStatus.Failed, "The engine could not start. Check the diagnostic log."); }
        }
        finally { _gate.Release(); }
    }

    public void ReportStartupFailure(EngineException failure) =>
        _store.Publish(EngineStatus.Failed, failure.Message, failure: failure.Failure);

    public async Task CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store.Current.Status != EngineStatus.Ready) return;
            try
            {
                var info = await _engine.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                if (_store.Current.Status == EngineStatus.Ready)
                    _store.Publish(EngineStatus.Ready, info.RecoveryMessage ?? "Ready · local connection verified", info.Version);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (EngineException ex) { _store.Publish(EngineStatus.Failed, ex.Message, failure: ex.Failure); }
            catch (Exception) { _store.Publish(EngineStatus.Failed, "The engine connection was lost. Restart the engine to reconnect."); }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Publish(EngineStatus.Stopping, "Stopping aria2…");
            await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
            _store.Publish(EngineStatus.Stopped, "Engine stopped");
        }
        finally { _gate.Release(); }
    }

    private void OnUnexpectedExit(object? sender, EventArgs args) =>
        _store.Publish(EngineStatus.Failed, "aria2 stopped unexpectedly. Bounded recovery will check its saved session.",
            failure: EngineFailure.ProcessExited);

    public async Task MonitorOnceAsync(CancellationToken token = default)
    {
        await _monitorGate.WaitAsync(token).ConfigureAwait(false);
        try { await MonitorCoreAsync(token).ConfigureAwait(false); }
        finally { _monitorGate.Release(); }
    }
    private async Task MonitorCoreAsync(CancellationToken token)
    {
        if (_store.Current.Status == EngineStatus.Ready)
        {
            await CheckHealthAsync(token).ConfigureAwait(false);
            if (_store.Current.Status == EngineStatus.Ready && _readySince is { } ready && _clock.GetUtcNow() - ready >= TimeSpan.FromSeconds(30))
            { _restartAttempts = 0; _nextRestart = default; }
        }
        if (_store.Current.Status != EngineStatus.Failed || _store.Current.Failure is not
            (EngineFailure.ProcessExited or EngineFailure.RpcUnavailable or EngineFailure.StartupTimeout)) return;
        if (_restartAttempts >= 3)
        {
            if (!_store.Current.Message.Contains("Automatic recovery stopped", StringComparison.Ordinal))
                _store.Publish(EngineStatus.Failed, "Automatic recovery stopped after three attempts. Check the cause and use Start engine to retry.", failure: _store.Current.Failure);
            return;
        }
        if (_nextRestart == default) _nextRestart = _clock.GetUtcNow() + TimeSpan.FromSeconds(2);
        if (_clock.GetUtcNow() < _nextRestart) return;
        _restartAttempts++;
        _nextRestart = _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Pow(2, _restartAttempts + 1));
        await StartCoreAsync(false, token).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _monitoring, 1) != 0) throw new InvalidOperationException("Only one engine monitor is allowed.");
        try
        {
            await StartAsync(token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _clock);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) await MonitorOnceAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Volatile.Write(ref _monitoring, 0); }
    }

    public async Task OnSuspendAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { if (_store.Current.Status == EngineStatus.Ready) await _engine.CheckpointAsync(token).ConfigureAwait(false); }
        catch (EngineException) { /* Preserve existing checkpoints; resume rechecks the connection. */ }
        finally { _gate.Release(); }
    }
    public Task OnResumeAsync(CancellationToken token = default) => MonitorOnceAsync(token);

    public void Dispose()
    {
        _engine.UnexpectedExit -= OnUnexpectedExit;
        GC.SuppressFinalize(this);
    }
}
