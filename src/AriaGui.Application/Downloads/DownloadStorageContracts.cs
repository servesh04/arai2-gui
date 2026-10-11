namespace AriaGui.Application.Downloads;

public interface IDownloadRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DownloadEntry>> LoadAsync(CancellationToken cancellationToken);
    async Task<IReadOnlyList<DownloadEntry>> LoadCurrentAsync(CancellationToken token) => DownloadBrowsing.RetainRecent(await LoadAsync(token));
    async Task<IReadOnlyList<string>> LoadEngineIdsAsync(CancellationToken token) => (await LoadAsync(token)).Select(row => row.Attempt.EngineId).ToArray();
    async Task<DownloadEntry?> GetAsync(Guid id, CancellationToken token) => (await LoadAsync(token)).SingleOrDefault(row => row.Job.Id == id);
    async Task<bool> ContainsDestinationAsync(string path, CancellationToken token) => (await LoadAsync(token)).Any(row => string.Equals(row.Job.Destination, path, StringComparison.OrdinalIgnoreCase));
    async Task<HistoryPage> LoadPageAsync(HistoryQuery query, CancellationToken token)
    {
        query.Validate();
        var rows = (await LoadAsync(token)).Where(row => DownloadBrowsing.IsTerminal(row.Progress.Status) && DownloadBrowsing.Matches(row, query.Search, query.Filter))
            .OrderByDescending(row => row.Job.CreatedAt).ThenBy(row => row.Job.Id).ToArray();
        return new(rows.Skip(checked(query.Page * query.PageSize)).Take(query.PageSize).ToArray(), rows.Length, query.Page, query.PageSize);
    }
    Task InsertIntentAsync(DownloadEntry entry, CancellationToken cancellationToken);
    Task SaveAsync(IReadOnlyList<DownloadEntry> entries, CancellationToken cancellationToken);
    Task InsertOperationAsync(DownloadEntry entry, CancellationToken token) => throw new NotSupportedException();
    Task InsertRecoveryOperationAsync(DownloadEntry entry, CancellationToken token) => throw new NotSupportedException();
    Task InsertRetryIntentAsync(DownloadEntry previous, DownloadEntry retry, CancellationToken token) => throw new NotSupportedException();
    Task<QueuePolicy> LoadQueuePolicyAsync(CancellationToken token) => Task.FromResult(new QueuePolicy());
    Task SaveQueuePolicyAsync(QueuePolicy policy, CancellationToken token) => Task.CompletedTask;
    async Task<IReadOnlyList<Guid>> SaveAndReportCompletionsAsync(IReadOnlyList<DownloadEntry> entries, CancellationToken token)
    { await SaveAsync(entries, token); return Array.Empty<Guid>(); }
}

public sealed record QueuePolicy(int Limit = 3, int? RequestedLimit = null, Guid? OperationId = null,
    AriaGui.Core.Downloads.OperationStatus? RequestStatus = null);

public sealed record DownloadTarget(string Directory, string FileName, bool ContinueExisting = false)
{
    public string FullPath => Path.Combine(Directory, FileName);
}

public interface IDownloadDestination
{
    Task<DownloadTarget> ValidateAsync(Uri source, string directory, string? fileName, CancellationToken cancellationToken);
    Task<DownloadTarget> ValidateRetryAsync(Uri source, string directory, string fileName, CancellationToken token) =>
        ValidateAsync(source, directory, fileName, token);
}

public sealed class DownloadStorageException(string message) : Exception(message);
public sealed class DownloadRequestException(string message, AriaGui.Browser.Contracts.RejectionReason? reason = null) : Exception(message)
{ public AriaGui.Browser.Contracts.RejectionReason? Reason { get; } = reason; }
