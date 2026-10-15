using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed partial class DownloadCoordinator
{
    public async Task SaveBandwidthProfilesAsync(IReadOnlyList<BandwidthProfile> profiles, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        await InitializeAsync(token).ConfigureAwait(false);
        await _scheduleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var saved = _schedule with { SavedProfiles = profiles.ToArray() };
                saved.Validate();
                await repository.SaveBandwidthScheduleAsync(saved, token).ConfigureAwait(false);
                _schedule = saved; _epoch++;
                store.Publish(store.Current.Downloads, store.Current.Connection, schedule: saved);
            }
            finally { _gate.Release(); }
        }
        finally { _scheduleGate.Release(); }
    }

    public async Task SetBandwidthScheduleAsync(BandwidthSchedule schedule, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _scheduleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                schedule = schedule with { SavedProfiles = schedule.SavedProfiles ?? _schedule.SavedProfiles,
                    ResetDefaultOnDisable = !schedule.Enabled &&
                    (_schedule.Enabled || _schedule.ResetDefaultOnDisable || schedule.ResetDefaultOnDisable) };
                schedule.Validate();
                await repository.SaveBandwidthScheduleAsync(schedule, token).ConfigureAwait(false);
                _schedule = schedule; _epoch++;
                store.Publish(store.Current.Downloads, store.Current.Connection, schedule: schedule);
            }
            finally { _gate.Release(); }
        }
        finally { _scheduleGate.Release(); }
        await EvaluateScheduleAsync(token).ConfigureAwait(false);
    }

    // The host may call either hook after a wake, clock or timezone notification.
    // Polling also reevaluates current wall time, so no missed boundaries are replayed.
    public Task NotifyClockChangedAsync(CancellationToken token = default) => RefreshAsync(token);
    public Task NotifyWakeAsync(CancellationToken token = default) => RefreshAsync(token);

    private async Task EvaluateScheduleAsync(CancellationToken token)
    {
        await _scheduleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var decision = _schedule.Evaluate(clock.GetUtcNow());
            if (store.Current.Connection != EngineConnectionState.Connected) return;
            var resetNeedsObservation = _schedule.ResetDefaultOnDisable &&
                (_bandwidth.RequestedRevision == 0 || _bandwidth.ConfirmedGeneration != _generation);
            if ((_schedule.Enabled || _schedule.ResetDefaultOnDisable) && _bandwidth.RequestStatus is not (OperationStatus.Pending or OperationStatus.Uncertain)
                && (_bandwidth.Requested != decision.Profile || resetNeedsObservation))
            {
                try { await SetBandwidthProfileAsync(decision.Profile, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* Durable uncertain/failed intent remains visible. */ }
            }
            if (_schedule.ResetDefaultOnDisable && _bandwidth.RequestStatus is null &&
                _bandwidth.Requested == _schedule.DefaultProfile && _bandwidth.Confirmed == _schedule.DefaultProfile &&
                _bandwidth.ConfirmedGeneration == _generation)
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var settled = _schedule with { ResetDefaultOnDisable = false };
                    await repository.SaveBandwidthScheduleAsync(settled, token).ConfigureAwait(false);
                    _schedule = settled; _epoch++;
                    store.Publish(store.Current.Downloads, store.Current.Connection, schedule: settled);
                }
                finally { _gate.Release(); }
            }
            foreach (var entry in store.Current.Downloads.ToArray())
            {
                if (entry.DesiredRemoved || IsTerminal(entry.Progress.Status) || entry.PendingOperation is not null || _inFlight.Contains(entry.Job.Id)) continue;
                if (decision.Blocked && !entry.DesiredPaused && !entry.PolicyHeld &&
                    entry.Progress.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
                {
                    try { await CommandAsync(entry.Job.Id, DownloadAction.Pause, null, token, policyCommand: true).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* Never replay an uncertain command. */ }
                }
                else if (!decision.Blocked && entry.PolicyHeld)
                {
                    if (!entry.DesiredPaused && entry.Progress.Status == DownloadStatus.Paused)
                    {
                        try { await CommandAsync(entry.Job.Id, DownloadAction.Resume, null, token, policyCommand: true).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { /* The saved operation carries uncertainty. */ }
                    }
                    else
                    {
                        await _gate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            var current = store.Current.Downloads.SingleOrDefault(row => row.Job.Id == entry.Job.Id);
                            if (current is null || current.Attempt.Id != entry.Attempt.Id || current.PendingOperation is not null) continue;
                            var released = current with { PolicyHeld = false };
                            await repository.SaveAsync([released], token).ConfigureAwait(false);
                            _epoch++; store.Publish(store.Current.Downloads.Select(row => row.Job.Id == entry.Job.Id ? released : row), store.Current.Connection);
                        }
                        finally { _gate.Release(); }
                    }
                }
            }
        }
        finally { _scheduleGate.Release(); }
    }
}
