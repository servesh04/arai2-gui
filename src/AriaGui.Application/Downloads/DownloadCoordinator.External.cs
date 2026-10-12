using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed partial class DownloadCoordinator
{
    public async Task DeleteTerminalHistoryAsync(Guid id, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_inFlight.Contains(id)) throw new DownloadRequestException("Wait for the active command before deleting history.");
            await ExternalRepository.DeleteTerminalHistoryAsync(id, clock.GetUtcNow(), token).ConfigureAwait(false);
            _epoch++; store.Publish(store.Current.Downloads.Where(row => row.Job.Id != id), store.Current.Connection);
        }
        finally { _gate.Release(); }
    }
    internal IExternalRequestRepository ExternalRepository => repository as IExternalRequestRepository
        ?? throw new NotSupportedException("External intake requires the durable repository.");
    internal Task<DownloadEntry?> LoadExternalJobAsync(Guid id, CancellationToken token) => repository.GetAsync(id, token);
    internal async Task<DownloadEntry> PrepareExternalAsync(Uri source, string folder, string? name, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        if (engine.SupportsFreshRestart) await destination.ValidateProtectedAsync(folder, token).ConfigureAwait(false);
        var target = await destination.ValidateAsync(source, folder, name, token).ConfigureAwait(false);
        if (engine.SupportsFreshRestart) await destination.ValidateProtectedAsync(target.Directory, token).ConfigureAwait(false);
        return BuildIntent(source, target, clock.GetUtcNow()) with { ExactSourceUrl = source.OriginalString };
    }
    private static bool IsDispatchableExternalIntent(DownloadEntry entry) => entry.Progress.Status == DownloadStatus.Queued
        && !entry.DesiredPaused && !entry.DesiredRemoved
        && entry.PendingOperation is { Action: DownloadAction.Add, Status: OperationStatus.Pending };
    internal async Task DispatchExternalAsync(Guid id, CancellationToken applicationLifetime)
    {
        await InitializeAsync(applicationLifetime).ConfigureAwait(false);
        DownloadEntry? entry;
        await _gate.WaitAsync(applicationLifetime).ConfigureAwait(false);
        try
        {
            entry = await repository.GetAsync(id, applicationLifetime).ConfigureAwait(false);
            if (entry is null || !IsDispatchableExternalIntent(entry) || _inFlight.Contains(id)) return;
            // Persist BEFORE sending. A crash immediately after this claim is conservatively ambiguous.
            if (!await ExternalRepository.ClaimExternalDispatchAsync(id, clock.GetUtcNow(), applicationLifetime).ConfigureAwait(false)) return;
            _inFlight.Add(id); _epoch++;
            if (!store.Current.Downloads.Any(row => row.Job.Id == id)) store.Publish(store.Current.Downloads.Append(entry), store.Current.Connection);
        }
        finally { _gate.Release(); }
        await DispatchAddAsync(entry, false, applicationLifetime).ConfigureAwait(false);
    }
}
