using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed partial class DownloadCoordinator
{
    // Browser credentials are held by the relay. Only its local URL reaches durable intent and aria2.
    public async Task<DownloadEntry> AddBrowserRelayAsync(Uri relay, string folder, string fileName, bool startPaused,
        CancellationToken token)
    {
        if (relay.Scheme != "http" || !relay.IsLoopback) throw new DownloadRequestException("Invalid browser relay.");
        await InitializeAsync(token).ConfigureAwait(false);
        if (engine.SupportsFreshRestart) await destination.ValidateProtectedAsync(folder, token).ConfigureAwait(false);
        var target = await destination.ValidateAsync(relay, folder, fileName, token).ConfigureAwait(false);
        if (engine.SupportsFreshRestart) await destination.ValidateProtectedAsync(target.Directory, token).ConfigureAwait(false);
        var entry = BuildIntent(relay, target, clock.GetUtcNow()) with
        { DesiredPaused = startPaused, PolicyHeld = _schedule.Enabled };
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (await repository.ContainsDestinationAsync(target.FullPath, token).ConfigureAwait(false))
                throw new DownloadRequestException("History already contains this destination.");
            await repository.InsertIntentAsync(entry, token).ConfigureAwait(false);
            _inFlight.Add(entry.Job.Id); _epoch++;
            store.Publish(store.Current.Downloads.Append(entry), store.Current.Connection);
        }
        finally { _gate.Release(); }
        try
        {
            var result = await engine.AddAsync(new(entry.Attempt.EngineId, relay, Directory: target.Directory,
                FileName: target.FileName, RequirePublicOrigin: false,
                StartPaused: startPaused || entry.PolicyHeld, ContinuityJobId: entry.Job.Id,
                ContinuityAttemptId: entry.Attempt.Id), token).ConfigureAwait(false);
            await CommitAsync(ApplyObservation(entry, result), CancellationToken.None).ConfigureAwait(false);
            return entry;
        }
        catch
        {
            await CommitAsync(entry with
            {
                PendingOperation = entry.PendingOperation! with { Status = OperationStatus.Uncertain },
                ErrorMessage = "Browser handoff add is uncertain. Checking the saved engine ID."
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { _inFlight.Remove(entry.Job.Id); _epoch++; }
            finally { _gate.Release(); }
        }
    }

    public async Task<DownloadEntry?> GetBrowserEntryAsync(Guid id, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        return await repository.GetAsync(id, token).ConfigureAwait(false);
    }
}
