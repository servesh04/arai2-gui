using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public enum BatchOutcome { Confirmed, Unconfirmed, Skipped, Failed, Canceled }
public sealed record DownloadBatchResult(Guid JobId, BatchOutcome Outcome, string Message);
public static class DownloadBatch
{
    public static bool CanExecute(DownloadEntry entry, bool connected, DownloadAction action)
    {
        if (!connected) return false;
        if (action == DownloadAction.Recover)
            return entry.PendingOperation?.Status != OperationStatus.Pending &&
                (entry.Progress.Status == DownloadStatus.Interrupted || entry.PendingOperation?.Status == OperationStatus.Uncertain);
        if (entry.PendingOperation is not null) return false;
        return action switch
        {
            DownloadAction.Pause or DownloadAction.Remove => entry.Progress.Status is DownloadStatus.Downloading or DownloadStatus.Queued
                || action == DownloadAction.Remove && entry.Progress.Status == DownloadStatus.Paused,
            DownloadAction.Resume => entry.Progress.Status == DownloadStatus.Paused,
            DownloadAction.Retry => entry.Progress.Status is DownloadStatus.Failed or DownloadStatus.Removed,
            _ => false
        };
    }
    public static async Task<IReadOnlyList<DownloadBatchResult>> ExecuteAsync(IEnumerable<Guid> selection, DownloadAction action,
        Func<Guid, DownloadEntry?> resolve, Func<bool> connected, Func<Guid, DownloadAction, CancellationToken, Task> execute, CancellationToken token,
        Func<DownloadEntry, bool>? eligibility = null)
    {
        var results = new List<DownloadBatchResult>();
        foreach (var id in selection.Distinct().ToArray())
        {
            if (token.IsCancellationRequested) { results.Add(new(id, BatchOutcome.Canceled, "Canceled before submission.")); continue; }
            var entry = resolve(id);
            if (entry is null || !connected() || !(eligibility?.Invoke(entry) ?? CanExecute(entry, true, action))) { results.Add(new(id, BatchOutcome.Skipped, "No longer eligible or engine disconnected.")); continue; }
            try
            {
                await execute(id, action, token);
                var updated = resolve(id);
                var confirmed = updated is not null && updated.PendingOperation is null && (action switch
                {
                    DownloadAction.Pause => updated.Progress.Status == DownloadStatus.Paused,
                    DownloadAction.Resume => updated.Progress.Status is DownloadStatus.Downloading or DownloadStatus.Queued,
                    DownloadAction.Remove => updated.Progress.Status == DownloadStatus.Removed,
                    DownloadAction.Retry => updated.Attempt.Id != entry.Attempt.Id,
                    DownloadAction.Recover => updated.Progress.Status != DownloadStatus.Interrupted,
                    _ => false
                });
                results.Add(new(id, confirmed ? BatchOutcome.Confirmed : BatchOutcome.Unconfirmed,
                    confirmed ? "Confirmed." : "Submitted; awaiting confirmation. Check the saved intent."));
            }
            catch (Exception ex)
            {
                var savedIntent = resolve(id)?.PendingOperation;
                var submitted = savedIntent is not null && savedIntent.Action == action
                    && savedIntent.Status is OperationStatus.Pending or OperationStatus.Uncertain;
                results.Add(new(id, submitted ? BatchOutcome.Unconfirmed : ex is OperationCanceledException ? BatchOutcome.Canceled : BatchOutcome.Failed,
                    submitted ? "Saved intent is awaiting confirmation." : ex is OperationCanceledException ? "Canceled before confirmation."
                    : ex is DownloadRequestException or DownloadStorageException ? ex.Message : "Command failed; check the row and engine connection."));
            }
        }
        return results;
    }
}
