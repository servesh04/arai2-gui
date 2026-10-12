using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Tests;

public sealed class DurableDownloadTests
{
    [Fact]
    public async Task Committed_intent_exists_before_the_first_engine_mutation()
    {
        var repository = new MemoryDownloadRepository();
        var engine = new InspectableClient(request =>
        {
            var saved = Assert.Single(repository.Entries.Values);
            Assert.Equal(request.EngineId, saved.Attempt.EngineId);
            Assert.Equal(OperationStatus.Pending, saved.PendingOperation?.Status);
            Assert.False(request.RequirePublicOrigin); // Explicit manual intake, not caller-controlled browser JSON.
        });
        await Coordinator(engine, repository, new()).AddAsync("https://example.com/file");
        Assert.Null(Assert.Single(repository.Entries.Values).PendingOperation);
    }

    [Fact]
    public async Task Failed_intent_commit_prevents_the_engine_mutation()
    {
        var repository = new MemoryDownloadRepository { RejectInsert = true };
        var engine = new InspectableClient();
        var store = new DownloadRuntimeStore();
        await Assert.ThrowsAsync<DownloadStorageException>(() => Coordinator(engine, repository, store).AddAsync("https://example.com/file"));
        Assert.Equal(0, engine.AddCount);
        Assert.Empty(store.Current.Downloads);
    }

    [Fact]
    public async Task Polling_during_inflight_add_does_not_mark_it_missing_or_resubmit_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new InspectableClient { Release = release.Task, Started = started };
        var store = new DownloadRuntimeStore();
        var coordinator = Coordinator(engine, new(), store);
        var add = coordinator.AddAsync("https://example.com/file");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(OperationStatus.Pending, Assert.Single(store.Current.Downloads).PendingOperation?.Status);
        release.SetResult();
        await add;
        Assert.Null(Assert.Single(store.Current.Downloads).PendingOperation);
        Assert.Equal(1, engine.AddCount);
    }

    [Fact]
    public async Task Cancellation_after_intent_keeps_uncertainty_durable_and_restart_does_not_resubmit()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new InspectableClient { Release = Task.Delay(Timeout.InfiniteTimeSpan), Started = started };
        var repository = new MemoryDownloadRepository();
        using var cancellation = new CancellationTokenSource();
        var add = Coordinator(engine, repository, new()).AddAsync("https://example.com/file", cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => add);
        Assert.Equal(OperationStatus.Uncertain, Assert.Single(repository.Entries.Values).PendingOperation?.Status);
        var restartedStore = new DownloadRuntimeStore();
        await Coordinator(engine, repository, restartedStore).RefreshAsync();
        Assert.Equal(DownloadStatus.Interrupted, Assert.Single(restartedStore.Current.Downloads).Progress.Status);
        Assert.Equal(1, engine.AddCount);
    }

    private static DownloadCoordinator Coordinator(IAria2Client engine, MemoryDownloadRepository repository, DownloadRuntimeStore store) =>
        new(engine, store, TimeProvider.System, repository, new TestDownloadDestination());

    [Fact]
    public async Task Fresh_restart_refusal_does_not_create_an_attempt_or_supersede_uncertainty()
    {
        var repository = new MemoryDownloadRepository(); var engine = new InspectableClient { SupportsFreshRestart = true };
        var store = new DownloadRuntimeStore(); var coordinator = Coordinator(engine, repository, store);
        await coordinator.AddAsync("https://example.com/file");
        var previous = Assert.Single(repository.Entries.Values);
        var uncertain = new PendingOperation(Guid.NewGuid(), previous.Job.Id, DownloadAction.Recover, OperationStatus.Uncertain, DateTimeOffset.UtcNow);
        await repository.SaveAsync([previous with { Progress = new(DownloadStatus.Interrupted, 0, null, 0), PendingOperation = uncertain }], default);
        var restarted = Coordinator(engine, repository, new());
        await Assert.ThrowsAsync<DownloadRequestException>(() => restarted.RestartFreshAsync(previous.Job.Id));
        Assert.Single(repository.Attempts); Assert.Single(repository.Intents); Assert.Equal(1, engine.AddCount);
        Assert.Equal(uncertain, Assert.Single(repository.Entries.Values).PendingOperation);
        Assert.Equal(previous.Attempt.Id, Assert.Single(repository.Entries.Values).Attempt.Id);
    }
    private sealed class InspectableClient(Action<EngineDownloadRequest>? inspect = null) : IAria2Client
    {
        public int AddCount;
        public bool SupportsFreshRestart { get; init; }
        public Task? Release { get; init; }
        public TaskCompletionSource? Started { get; init; }
        public async Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        {
            AddCount++;
            inspect?.Invoke(request);
            Started?.TrySetResult();
            if (Release is not null) await Release.WaitAsync(token);
            return new(request.EngineId, new(DownloadStatus.Queued, 0, null, 0));
        }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<EngineDownload>>([]);
    }
}
