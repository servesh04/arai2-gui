using System.Diagnostics;
using System.Text.Json;
using AriaGui.App.Services;
using AriaGui.App.ViewModels;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace AriaGui.Application.Tests;

public sealed class ScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Ten_thousand_history_rows_and_one_hundred_live_transfers_have_bounded_runtime_and_ui_work()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.ScaleTests", Guid.NewGuid().ToString("N"));
        var database = Path.Combine(root, "downloads.db"); var repository = new SqliteDownloadRepository(database);
        try
        {
            await repository.InitializeAsync(default);
            var engine = new ScaleEngine(); var oldFailed = Guid.Empty;
            using (var db = new DownloadDbContext(new DbContextOptionsBuilder<DownloadDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options))
            {
                var time = DateTimeOffset.UtcNow.AddDays(-1).UtcTicks;
                for (var index = 0; index < 10100; index++)
                {
                    var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var name = $"history-{index:00000}.bin"; var gid = (index + 1).ToString("x16");
                    var status = index < 10000 ? (index % 3) switch { 0 => DownloadStatus.Failed, 1 => DownloadStatus.Completed, _ => DownloadStatus.Removed } : DownloadStatus.Downloading;
                    if (index == 0) oldFailed = job;
                    db.Jobs.Add(new() { Id = job, DisplayName = name, Source = "https://example.com/" + name, Destination = Path.Combine(root, name), DestinationKey = Path.Combine(root, name).ToUpperInvariant(), CreatedAtUtcTicks = time + index });
                    db.Attempts.Add(new() { Id = attempt, JobId = job, EngineId = gid, StartedAtUtcTicks = time + index, Status = status, TotalBytes = 100000, UpdatedAtUtcTicks = time });
                    db.Files.Add(new() { Id = Guid.NewGuid(), JobId = job, AttemptId = attempt, FileName = name, TotalBytes = 100000 });
                    if (index >= 10000) engine.Live.Add(gid);
                }
                db.SaveChanges();
            }
            await repository.LoadCurrentAsync(default); await repository.LoadPageAsync(new(), default); // Warm metadata/query compilation.
            var timer = Stopwatch.StartNew(); var current = await repository.LoadCurrentAsync(default); var currentMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(200, current.Count); Assert.Equal(100, current.Count(row => row.Progress.Status == DownloadStatus.Downloading));
            timer.Restart(); var first = await repository.LoadPageAsync(new(), default); var pageMs = timer.Elapsed.TotalMilliseconds;
            var second = await repository.LoadPageAsync(new(Page: 1), default);
            Assert.Equal(10000, first.Total); Assert.Equal(50, first.Entries.Count); Assert.Empty(first.Entries.Select(row => row.Job.Id).Intersect(second.Entries.Select(row => row.Job.Id)));
            var filtered = await repository.LoadPageAsync(new("HISTORY-099", DownloadFilter.Completed), default);
            Assert.All(filtered.Entries, row => { Assert.Contains("history-099", row.Job.DisplayName); Assert.Equal(DownloadStatus.Completed, row.Progress.Status); });
            Assert.InRange(filtered.Total, 1, 100);
            var managedBefore = GC.GetTotalMemory(true);
            var store = new DownloadRuntimeStore(); var coordinator = new DownloadCoordinator(engine, store, TimeProvider.System, repository, new TestDownloadDestination());
            await coordinator.InitializeAsync(); var dispatcher = new QueueDispatcher();
            using var list = new DownloadsViewModel(coordinator, store, dispatcher, repository: repository);
            using var summary = new DownloadSummaryViewModel(store, dispatcher);
            for (var index = 0; index < 50; index++) await coordinator.RefreshAsync();
            Assert.Equal(2, dispatcher.Count); // One pending UI callback per observer, independent of refresh count.
            timer.Restart(); dispatcher.Drain(); var uiMs = timer.Elapsed.TotalMilliseconds;
            var managedGrowth = Math.Max(0, GC.GetTotalMemory(true) - managedBefore);
            Assert.Equal(200, list.Downloads.Count); Assert.Equal("100", summary.Active); Assert.Equal(1, engine.MaxConcurrent);
            var row = list.Downloads.First(); await coordinator.RefreshAsync(); dispatcher.Drain(); Assert.Contains(row, list.Downloads);
            list.IsHistoryView = true; await list.BrowsingTask; dispatcher.Drain(); Assert.Equal(50, list.Downloads.Count);
            list.NextPageCommand.Execute(null); await list.BrowsingTask; dispatcher.Drain(); Assert.Equal(50, list.Downloads.Count);
            // A job outside the recent window can still be retried without loading all history.
            Assert.DoesNotContain(store.Current.Downloads, row => row.Job.Id == oldFailed);
            await coordinator.RetryAsync(oldFailed); Assert.Contains(store.Current.Downloads, row => row.Job.Id == oldFailed && row.Progress.Status == DownloadStatus.Queued);
            var updated = await repository.LoadPageAsync(new(), default); Assert.Equal(9999, updated.Total);
            var metrics = new { recordedAtUtc = DateTimeOffset.UtcNow, os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                runtime = Environment.Version.ToString(), configuration = "Debug, .NET tests (no native renderer)", historyRows = 10000, activeRows = 100,
                residentRows = current.Count, historyPageRows = first.Entries.Count, pendingUiCallbacks = 2, maxConcurrentPolls = engine.MaxConcurrent,
                loadCurrentMs = currentMs, historyPageMs = pageMs, applyLatestSnapshotMs = uiMs,
                retainedManagedGrowthBytes = managedGrowth,
                budgets = new { databaseQueryMs = 1000, applySnapshotMs = 100, managedGrowthBytes = 16 * 1024 * 1024, recentRecords = 100, historyPageRecords = 50 } };
            var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }); output.WriteLine(json);
            var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            if (File.Exists(Path.Combine(workspace, "AriaGui.slnx")))
            { Directory.CreateDirectory(Path.Combine(workspace, "artifacts")); await File.WriteAllTextAsync(Path.Combine(workspace, "artifacts", "phase6-profile.json"), json); }
            Assert.True(currentMs < 1000, $"Current query exceeded 1000 ms: {currentMs:F1}");
            Assert.True(pageMs < 1000, $"History query exceeded 1000 ms: {pageMs:F1}");
            Assert.True(uiMs < 100, $"Snapshot apply exceeded 100 ms: {uiMs:F1}");
            Assert.True(managedGrowth < 16 * 1024 * 1024, $"Managed memory growth exceeded 16 MiB: {managedGrowth}");
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.ScaleTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Search_filters_and_history_paging_preserve_rows_and_do_not_poll_historical_ids()
    {
        var repository = new MemoryDownloadRepository(); var engine = new ScaleEngine(); var time = DateTimeOffset.UtcNow;
        for (var index = 0; index < 130; index++)
        {
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var name = $"item-{index:000}.bin";
            repository.Entries[job] = new(new(job, name, new("https://example.com/" + name), @"C:\downloads\" + name, time.AddSeconds(index)),
                new(attempt, job, (index + 1).ToString("x16"), time.AddSeconds(index)), new(Guid.NewGuid(), job, attempt, name, 100), new(DownloadStatus.Completed, 100, 100, 0));
        }
        var store = new DownloadRuntimeStore(); var coordinator = new DownloadCoordinator(engine, store, TimeProvider.System, repository, new TestDownloadDestination());
        await coordinator.InitializeAsync(); Assert.Equal(100, store.Current.Downloads.Count);
        var dispatcher = new QueueDispatcher(); using var list = new DownloadsViewModel(coordinator, store, dispatcher, repository: repository); dispatcher.Drain();
        list.IsHistoryView = true; await list.BrowsingTask; dispatcher.Drain(); Assert.Equal(50, list.Downloads.Count);
        list.SearchText = "no-result"; list.SearchText = "ITEM-001"; await list.BrowsingTask; dispatcher.Drain();
        Assert.Equal("item-001.bin", Assert.Single(list.Downloads).FileName);
        list.FilterIndex = (int)DownloadFilter.Failed; await list.BrowsingTask; dispatcher.Drain(); Assert.Empty(list.Downloads); Assert.True(list.IsEmpty);
        await coordinator.RefreshAsync(); Assert.Equal(0, engine.LastRequested);
        list.IsHistoryView = false; list.SearchText = ""; list.FilterIndex = 0; Assert.Equal(100, list.Downloads.Count);
    }

    [Fact]
    public async Task Poll_backoff_and_file_actions_follow_state_and_report_missing_files()
    {
        var repository = new MemoryDownloadRepository(); var engine = new ScaleEngine(); var store = new DownloadRuntimeStore();
        var coordinator = new DownloadCoordinator(engine, store, TimeProvider.System, repository, new TestDownloadDestination());
        await coordinator.InitializeAsync(); Assert.Equal(TimeSpan.FromSeconds(5), coordinator.PollDelay);
        await coordinator.AddAsync("https://example.com/file.bin"); Assert.Equal(TimeSpan.FromSeconds(2), coordinator.PollDelay);
        var entry = store.Current.Downloads[0]; var completed = entry with { Progress = new(DownloadStatus.Completed, 100, 100, 0) };
        await repository.SaveAsync([completed], default);
        var reopened = new DownloadRuntimeStore(); var next = new DownloadCoordinator(engine, reopened, TimeProvider.System, repository, new TestDownloadDestination());
        await next.InitializeAsync(); using var list = new DownloadsViewModel(next, reopened, new ImmediateDispatcher(), fileActions: new MissingFileActions());
        var row = Assert.Single(list.Downloads); Assert.True(row.OpenFileCommand.CanExecute(null));
        await row.OpenFileCommand.ExecuteAsync(null); Assert.Contains("missing", list.ErrorMessage); Assert.Equal(DownloadStatus.Completed, reopened.Current.Downloads[0].Progress.Status);
    }
    [Fact]
    public async Task An_older_transfer_finishing_now_remains_in_the_recent_window_and_is_saved_before_trimming()
    {
        var repository = new MemoryDownloadRepository(); var engine = new ScaleEngine { Status = DownloadStatus.Completed };
        var time = DateTimeOffset.UtcNow.AddDays(-1);
        for (var index = 0; index < 131; index++)
        {
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var started = index == 130 ? time.AddDays(-30) : time.AddSeconds(index);
            var gid = (index + 1).ToString("x16"); var name = $"old-{index}.bin";
            repository.Entries[job] = new(new(job, name, new("https://example.com/" + name), @"C:\downloads\" + name, started),
                new(attempt, job, gid, started), new(Guid.NewGuid(), job, attempt, name, 100000),
                new(index == 130 ? DownloadStatus.Downloading : DownloadStatus.Completed, index == 130 ? 0 : 100000, 100000, 0), ObservedAt: started);
            if (index == 130) engine.Live.Add(gid);
        }
        var old = repository.Entries.Values.Single(row => row.Progress.Status == DownloadStatus.Downloading);
        var store = new DownloadRuntimeStore(); var coordinator = new DownloadCoordinator(engine, store, TimeProvider.System, repository, new TestDownloadDestination());
        await coordinator.InitializeAsync(); Assert.Equal(101, store.Current.Downloads.Count);
        await coordinator.RefreshAsync(); Assert.Equal(100, store.Current.Downloads.Count);
        Assert.Contains(store.Current.Downloads, row => row.Job.Id == old.Job.Id && row.Progress.Status == DownloadStatus.Completed);
        Assert.Equal(DownloadStatus.Completed, repository.Entries[old.Job.Id].Progress.Status); Assert.Equal(old.Attempt.Id, store.Current.LatestCompletion?.AttemptId);
    }
    private sealed class MissingFileActions : IDownloadFileActions
    { public Task OpenFileAsync(string path, CancellationToken token) => throw new DownloadRequestException("The completed file is missing."); public Task OpenFolderAsync(string path, CancellationToken token) => Task.CompletedTask; }
    private sealed class ImmediateDispatcher : IUiDispatcher { public void Enqueue(Action action) => action(); }
    private sealed class QueueDispatcher : IUiDispatcher
    { private readonly Queue<Action> _queue = new(); public int Count => _queue.Count; public void Enqueue(Action action) => _queue.Enqueue(action); public void Drain() { while (_queue.TryDequeue(out var action)) action(); } }
    private sealed class ScaleEngine : IAria2Client
    {
        public HashSet<string> Live { get; } = []; public int MaxConcurrent, LastRequested; private int _concurrent; private long _bytes;
        public DownloadStatus Status = DownloadStatus.Downloading;
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        { return Task.FromResult(new EngineDownload(request.EngineId, new(DownloadStatus.Queued, 0, 100000, 0))); }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.FromResult<IReadOnlyList<EngineDownload>>([]);
        public async Task<EngineObservation> ObserveAsync(IReadOnlyList<string> ids, CancellationToken token)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _concurrent)); LastRequested = ids.Count;
            try { await Task.Yield(); _bytes++; return new("scale", ids.Where(Live.Contains).Select(id => new EngineDownload(id, new(Status, Status == DownloadStatus.Completed ? 100000 : _bytes, 100000, Status == DownloadStatus.Completed ? 0 : 1))).ToArray(), 3); }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }
}
