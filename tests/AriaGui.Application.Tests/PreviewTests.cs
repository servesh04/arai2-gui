using AriaGui.App.Services;
using AriaGui.App.ViewModels;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Simulation;

namespace AriaGui.Application.Tests;

public sealed class PreviewTests
{
    private readonly ManualClock _clock = new();
    private readonly DownloadRuntimeStore _store = new();

    private DownloadCoordinator CreateCoordinator() => new(new FakeAria2Client(_clock), _store, _clock, new MemoryDownloadRepository(), new TestDownloadDestination());

    [Fact]
    public async Task Add_establishes_job_attempt_and_file_with_distinct_identity()
    {
        await CreateCoordinator().AddAsync("https://example.com/my%20file.zip?token=not-displayed");
        var item = Assert.Single(_store.Current.Downloads);
        Assert.Equal("my file.zip", item.Job.DisplayName);
        Assert.Equal(item.Job.Id, item.Attempt.JobId);
        Assert.Equal(item.Attempt.Id, item.File.AttemptId);
        Assert.NotEqual(item.Job.Id, item.Attempt.Id);
        Assert.Matches("^[0-9a-f]{16}$", item.Attempt.EngineId);
        Assert.Null(item.PendingOperation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("file:///C:/private.txt")]
    [InlineData("ftp://example.com/file")]
    [InlineData("https://user:password@example.com/file")]
    public async Task Invalid_or_unsupported_input_does_not_add_a_job(string url)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateCoordinator().AddAsync(url));
        Assert.Empty(_store.Current.Downloads);
    }

    [Fact]
    public async Task Unknown_size_becomes_known_then_completes()
    {
        var coordinator = CreateCoordinator();
        await coordinator.AddAsync("https://example.com/video.webm", unknownSize: true);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await coordinator.RefreshAsync();
        Assert.Null(_store.Current.Downloads[0].Progress.TotalBytes);
        Assert.True(_store.Current.Downloads[0].Progress.DownloadedBytes > 0);
        _clock.Advance(TimeSpan.FromSeconds(10));
        await coordinator.RefreshAsync();
        Assert.NotNull(_store.Current.Downloads[0].Progress.TotalBytes);
        _clock.Advance(TimeSpan.FromMinutes(3));
        await coordinator.RefreshAsync();
        Assert.Equal(DownloadStatus.Completed, _store.Current.Downloads[0].Progress.Status);
        Assert.Equal(0, _store.Current.Downloads[0].Progress.BytesPerSecond);
    }

    [Fact]
    public async Task Late_first_poll_can_observe_completion_without_an_intermediate_active_poll()
    {
        var coordinator = CreateCoordinator();
        await coordinator.AddAsync("https://example.com/file.zip");
        _clock.Advance(TimeSpan.FromMinutes(3));
        await coordinator.RefreshAsync();
        Assert.Equal(DownloadStatus.Completed, _store.Current.Downloads[0].Progress.Status);
        _clock.Advance(TimeSpan.FromMinutes(-2));
        await coordinator.RefreshAsync();
        Assert.Equal(DownloadStatus.Completed, _store.Current.Downloads[0].Progress.Status);
    }

    [Fact]
    public async Task List_and_summary_observe_one_store_and_preserve_row_identity()
    {
        var coordinator = CreateCoordinator();
        var dispatcher = new ImmediateDispatcher();
        using var list = new DownloadsViewModel(coordinator, _store, dispatcher);
        using var summary = new DownloadSummaryViewModel(_store, dispatcher);
        list.InputUrl = "https://example.com/file.zip";
        await list.AddDownloadCommand.ExecuteAsync(null);
        var row = Assert.Single(list.Downloads);
        Assert.Equal("1 download", summary.Total);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await coordinator.RefreshAsync();
        Assert.Same(row, Assert.Single(list.Downloads));
        Assert.True(row.Progress > 0);
        Assert.Equal("1", summary.Active);
        Assert.False(list.IsEmpty);
    }

    [Fact]
    public async Task Ui_changes_are_dispatched_and_stale_deliveries_are_ignored()
    {
        var coordinator = CreateCoordinator();
        var dispatcher = new QueuedDispatcher();
        using var list = new DownloadsViewModel(coordinator, _store, dispatcher);
        using var summary = new DownloadSummaryViewModel(_store, dispatcher);
        await coordinator.AddAsync("https://example.com/file.zip");
        _clock.Advance(TimeSpan.FromSeconds(5));
        await coordinator.RefreshAsync();
        Assert.Empty(list.Downloads);
        dispatcher.DrainNewestFirst();
        Assert.Equal("Downloading", Assert.Single(list.Downloads).Status);
        Assert.Equal("1", summary.Active);
    }

    [Fact]
    public async Task Disposed_observers_do_not_receive_queued_updates()
    {
        var coordinator = CreateCoordinator();
        var dispatcher = new QueuedDispatcher();
        var list = new DownloadsViewModel(coordinator, _store, dispatcher);
        await coordinator.AddAsync("https://example.com/file.zip");
        list.Dispose();
        dispatcher.DrainNewestFirst();
        Assert.Empty(list.Downloads);
    }

    [Fact]
    public async Task Invalid_input_is_a_visible_validation_error_and_next_valid_add_clears_it()
    {
        var coordinator = CreateCoordinator();
        using var list = new DownloadsViewModel(coordinator, _store, new ImmediateDispatcher());
        list.InputUrl = "oops";
        await list.AddDownloadCommand.ExecuteAsync(null);
        Assert.True(list.HasError);
        Assert.Empty(list.Downloads);
        list.InputUrl = "https://example.com/good.zip";
        await list.AddDownloadCommand.ExecuteAsync(null);
        Assert.False(list.HasError);
        Assert.Single(list.Downloads);
    }

    [Fact]
    public async Task Lost_add_response_is_reconciled_without_resubmission()
    {
        var engine = new LostResponseEngine(new FakeAria2Client(_clock));
        var coordinator = new DownloadCoordinator(engine, _store, _clock, new MemoryDownloadRepository(), new TestDownloadDestination());
        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.AddAsync("https://example.com/file.zip"));
        Assert.Equal(OperationStatus.Uncertain, _store.Current.Downloads[0].PendingOperation?.Status);
        await coordinator.RefreshAsync();
        Assert.Null(Assert.Single(_store.Current.Downloads).PendingOperation);
        Assert.Equal(1, engine.AddCount);
    }

    [Fact]
    public async Task Concurrent_adds_keep_all_jobs_and_published_snapshots_are_immutable()
    {
        var coordinator = CreateCoordinator();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => coordinator.AddAsync($"https://example.com/file-{i}.zip")));
        var before = _store.Current;
        Assert.Equal(20, before.Downloads.Count);
        Assert.Equal(20, before.Downloads.Select(item => item.Job.Id).Distinct().Count());
        Assert.Throws<NotSupportedException>(() => ((IList<DownloadEntry>)before.Downloads).Clear());
        await coordinator.AddAsync("https://example.com/another.zip");
        Assert.Equal(20, before.Downloads.Count);
        Assert.Equal(21, _store.Current.Downloads.Count);
    }

    [Fact]
    public async Task Synchronization_has_one_loop_and_stops_on_cancellation()
    {
        var coordinator = CreateCoordinator();
        using var cancellation = new CancellationTokenSource();
        var loop = coordinator.RunAsync(cancellation.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(cancellation.Token));
        cancellation.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loop.IsCompletedSuccessfully);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly List<Action> _actions = [];
        public void Enqueue(Action action) => _actions.Add(action);
        public void DrainNewestFirst()
        {
            foreach (var action in _actions.AsEnumerable().Reverse()) action();
            _actions.Clear();
        }
    }

    private sealed class LostResponseEngine(IAria2Client inner) : IAria2Client
    {
        public int AddCount { get; private set; }
        public async Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken cancellationToken)
        {
            AddCount++;
            await inner.AddAsync(request, cancellationToken);
            throw new TimeoutException("Simulated lost response.");
        }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
            inner.GetDownloadsAsync(ids, cancellationToken);
    }
}
