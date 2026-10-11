using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Tests;

internal sealed class MemoryDownloadRepository : IDownloadRepository
{
    public Dictionary<Guid, DownloadEntry> Entries { get; } = [];
    public bool RejectInsert { get; set; }
    public bool RejectCommand { get; set; }
    public List<DownloadEntry> Intents { get; } = [];
    public List<DownloadEntry> Attempts { get; } = [];
    public QueuePolicy Policy { get; private set; } = new();
    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
    public Task<IReadOnlyList<DownloadEntry>> LoadAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<DownloadEntry>>(Entries.Values.ToArray());
    public Task InsertIntentAsync(DownloadEntry entry, CancellationToken token)
    {
        if (RejectInsert) throw new DownloadStorageException("Injected storage failure.");
        Entries.Add(entry.Job.Id, entry);
        Attempts.Add(entry); Intents.Add(entry);
        return Task.CompletedTask;
    }
    public Task SaveAsync(IReadOnlyList<DownloadEntry> entries, CancellationToken token)
    {
        foreach (var entry in entries) Entries[entry.Job.Id] = entry;
        return Task.CompletedTask;
    }
    public async Task<IReadOnlyList<Guid>> SaveAndReportCompletionsAsync(IReadOnlyList<DownloadEntry> entries, CancellationToken token)
    {
        var completed = entries.Where(entry => entry.Progress.Status == DownloadStatus.Completed
            && Entries[entry.Job.Id].Progress.Status != DownloadStatus.Completed).Select(entry => entry.Attempt.Id).ToArray();
        await SaveAsync(entries, token);
        return completed;
    }
    public Task InsertOperationAsync(DownloadEntry entry, CancellationToken token)
    {
        if (RejectCommand) throw new DownloadStorageException("Injected command storage failure.");
        Intents.Add(entry); Entries[entry.Job.Id] = entry;
        return Task.CompletedTask;
    }
    public Task InsertRecoveryOperationAsync(DownloadEntry entry, CancellationToken token) => InsertOperationAsync(entry, token);
    public Task InsertRetryIntentAsync(DownloadEntry previous, DownloadEntry retry, CancellationToken token)
    {
        Attempts.Add(retry); Intents.Add(retry); Entries[retry.Job.Id] = retry;
        return Task.CompletedTask;
    }
    public Task<QueuePolicy> LoadQueuePolicyAsync(CancellationToken token) => Task.FromResult(Policy);
    public Task SaveQueuePolicyAsync(QueuePolicy policy, CancellationToken token) { Policy = policy; return Task.CompletedTask; }
}
internal sealed class TestDownloadDestination : IDownloadDestination
{
    public Task<DownloadTarget> ValidateAsync(Uri source, string directory, string? name, CancellationToken token) =>
        Task.FromResult(new DownloadTarget(string.IsNullOrWhiteSpace(directory) ? @"C:\TestDownloads" : directory,
            string.IsNullOrWhiteSpace(name) ? Uri.UnescapeDataString(source.Segments.Last()).Trim('/') : name));
}
