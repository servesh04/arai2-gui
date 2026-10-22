using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.App.Services;
using AriaGui.App.ViewModels;

namespace AriaGui.Application.Tests;

public sealed class DesktopBehaviorTests
{
    [Fact]
    public void Torrent_waiting_pause_and_checking_publication_progress_use_one_row()
    {
        var id = Guid.NewGuid();
        var draft = TorrentTransferProjection.Map(new(id, "bundle", new string('a', 40),
            @"C:\Downloads", DateTimeOffset.UtcNow, TorrentStage.AwaitingSelection, 200, 100, 0, 0, 2, 1));
        var item = new DownloadItemViewModel(draft, (_, _, _) => Task.CompletedTask);
        item.Update(draft, connected: true);
        Assert.False(item.PauseCommand.CanExecute(null));

        var waiting = TorrentTransferProjection.Map(new(id, "bundle", new string('a', 40),
            @"C:\Downloads", DateTimeOffset.UtcNow, TorrentStage.Waiting, 200, 100, 20, 0, 2, 1));
        item.Update(waiting, connected: true);
        Assert.True(item.PauseCommand.CanExecute(null));
        Assert.True(item.ShowPause);

        var checking = TorrentTransferProjection.Map(new(id, "bundle", new string('a', 40),
            @"C:\Downloads", DateTimeOffset.UtcNow, TorrentStage.Checking, 200, 100, 20, 0, 2, 1,
            VerifiedBytes: 75));
        Assert.Equal(75, checking.TorrentVerifiedBytes);
        var publishing = TorrentTransferProjection.Publishing(checking, 65, 100);
        Assert.Equal(TorrentStage.Publishing, publishing.TorrentStage);
        Assert.Equal("Publishing to Downloads", publishing.SubstateText);
        Assert.Equal(65, publishing.Progress.DownloadedBytes);
        Assert.Equal(100, publishing.Progress.TotalBytes);
    }

    [Theory]
    [InlineData(TorrentStage.AwaitingSelection, null, DownloadStatus.Queued, null)]
    [InlineData(TorrentStage.Checking, null, DownloadStatus.Downloading, "Checking files")]
    [InlineData(TorrentStage.Downloading, "Fetching metadata", DownloadStatus.Downloading, "Fetching metadata")]
    [InlineData(TorrentStage.Downloading, "Connecting", DownloadStatus.Downloading, "Connecting")]
    [InlineData(TorrentStage.Downloading, "No peers yet", DownloadStatus.Downloading, "No peers yet")]
    [InlineData(TorrentStage.Verifying, null, DownloadStatus.Downloading, "Verifying")]
    [InlineData(TorrentStage.Completed, "Seeding", DownloadStatus.Downloading, "Seeding")]
    [InlineData(TorrentStage.Publishing, null, DownloadStatus.Downloading, "Publishing to Downloads")]
    [InlineData(TorrentStage.Completed, null, DownloadStatus.Completed, null)]
    [InlineData(TorrentStage.Failed, null, DownloadStatus.Failed, null)]
    public void T1_torrent_substates_keep_existing_filters(TorrentStage stage, string? activity,
        DownloadStatus expected, string? substate)
    {
        var row = TorrentTransferProjection.Map(new(Guid.NewGuid(), "bundle", new string('a', 40),
            @"C:\Downloads", DateTimeOffset.UtcNow, stage, 200, 100, 35, 9, 2, 1,
            stage == TorrentStage.Failed ? "Local tracker stopped." : null, activity));
        Assert.Equal(TransferKind.Torrent, row.Kind);
        Assert.Equal(expected, row.Progress.Status);
        Assert.Equal(substate, row.SubstateText);
        Assert.Equal(100, row.Progress.TotalBytes);
        Assert.Equal(stage == TorrentStage.Completed ? 100 : 35, row.Progress.DownloadedBytes);
        Assert.True(DownloadBrowsing.Matches(row, "", expected switch
        {
            DownloadStatus.Downloading => DownloadFilter.Active,
            DownloadStatus.Queued => DownloadFilter.Waiting,
            DownloadStatus.Completed => DownloadFilter.Completed,
            _ => DownloadFilter.Failed
        }));
        Assert.Equal(1, Enum.GetValues<DownloadFilter>().Skip(1)
            .Count(filter => DownloadBrowsing.Matches(row, "", filter)));
    }

    [Fact]
    public void T1_shared_collection_counts_torrent_once_in_rail_and_footer()
    {
        var store = new DownloadRuntimeStore();
        var coordinator = new DownloadCoordinator(new MutableEngine(), store, TimeProvider.System,
            new MemoryDownloadRepository(), new TestDownloadDestination());
        using var vm = new DownloadsViewModel(coordinator, store, new ImmediateDispatcher());
        using var summary = new DownloadSummaryViewModel(store, new ImmediateDispatcher());
        var row = TorrentTransferProjection.Map(new(Guid.NewGuid(), "bundle", new string('a', 40),
            @"C:\Downloads", DateTimeOffset.UtcNow, TorrentStage.Downloading, 200, 100, 40, 10, 2, 1));
        store.PublishTorrents([row]);
        Assert.Single(vm.Downloads);
        Assert.Equal(1, vm.SidebarCounts[DownloadFilter.All]);
        Assert.Equal(1, vm.SidebarCounts[DownloadFilter.Active]);
        Assert.Equal("1 active · 0 waiting", summary.QueueStatus);
        Assert.Equal("1 download", summary.Total);
        store.PublishTorrents([row with { Progress = new(DownloadStatus.Completed, 100, 100, 0) }]);
        Assert.Equal(1, vm.SidebarCounts[DownloadFilter.Completed]);
        Assert.Equal("1", summary.Completed);
    }
    [Fact]
    public async Task Selection_survives_progress_updates_explicit_sort_and_is_pruned_after_filter()
    {
        var first = Entry(DownloadStatus.Downloading); var second = Entry(DownloadStatus.Downloading);
        second = second with { Job = second.Job with { DisplayName = "other.bin" }, Attempt = second.Attempt with { EngineId = "0000000000000002" } };
        var repository = new MemoryDownloadRepository(); repository.Entries.Add(first.Job.Id, first); repository.Entries.Add(second.Job.Id, second);
        var engine = new MutableEngine(); engine.Rows[first.Attempt.EngineId] = new(DownloadStatus.Downloading, 1, 100, 10); engine.Rows[second.Attempt.EngineId] = new(DownloadStatus.Downloading, 2, 100, 20);
        var store = new DownloadRuntimeStore(); var coordinator = new DownloadCoordinator(engine, store, TimeProvider.System, repository, new TestDownloadDestination());
        await coordinator.RefreshAsync(); using var vm = new DownloadsViewModel(coordinator, store, new ImmediateDispatcher());
        vm.SetSort(DownloadSortColumn.Speed); var selectedRow = vm.Downloads[0]; vm.SetSelection([selectedRow.Id]);
        engine.Rows[first.Attempt.EngineId] = new(DownloadStatus.Downloading, 3, 100, 40);
        await coordinator.RefreshAsync(); Assert.Same(selectedRow, vm.Downloads[0]); Assert.Contains(selectedRow.Id, vm.SelectedIds); Assert.Equal(3, selectedRow.DownloadedBytes);
        vm.SortColumn = DownloadSortColumn.Name; Assert.Same(selectedRow, vm.Downloads[0]);
        vm.SortDescending = true; Assert.Same(selectedRow, vm.Downloads[1]); Assert.Contains(selectedRow.Id, vm.SelectedIds);
        vm.SearchText = "other"; Assert.Empty(vm.SelectedIds); Assert.Null(vm.SelectedItem);
    }
    [Fact]
    public async Task Layout_save_waits_for_full_save_and_preserves_saved_theme_and_folder_without_applied_event()
    {
        var repository = new BlockingPreferences(); var vm = new PreferencesViewModel(repository); await vm.LoadAsync(default);
        var applied = 0; vm.Applied += _ => applied++;
        vm.ThemeIndex = (int)AppTheme.Dark; vm.DefaultFolder = @"C:\Saved";
        var selectedInstallation = Guid.NewGuid(); vm.BrowserInstallationId = selectedInstallation.ToString("D"); vm.BrowserInstallRoot = @"C:\Saved browser 日本語";
        var full = vm.SaveCommand.ExecuteAsync(null); await repository.Entered.Task;
        vm.CompactRows = false; var layout = vm.SaveLayoutAsync(); vm.ThemeIndex = (int)AppTheme.Light; vm.DefaultFolder = @"C:\Draft";
        vm.BrowserInstallRoot = @"C:\Unsaved draft browser";
        repository.Release.TrySetResult(); await full; await layout;
        Assert.Equal(AppTheme.Dark, repository.Saved.Theme); Assert.Equal(@"C:\Saved", repository.Saved.DefaultFolder);
        Assert.Equal(selectedInstallation, repository.Saved.EffectiveBrowser.SelectedInstallationId); Assert.Equal(@"C:\Saved browser 日本語", repository.Saved.EffectiveBrowser.InstallRoot);
        Assert.False(repository.Saved.EffectiveLayout.CompactRows); Assert.Equal(1, applied); Assert.Equal(1, repository.MaxConcurrent);
    }
    [Fact]
    public async Task Torrent_notice_acceptance_survives_other_preferences_save()
    {
        var repository = new ImmediatePreferences();
        var vm = new PreferencesViewModel(repository);
        await vm.LoadAsync(default);
        Assert.False(vm.TorrentPrivacyNoticeAccepted);
        await vm.RememberTorrentPrivacyNoticeAsync(default);
        Assert.True(vm.TorrentPrivacyNoticeAccepted);
        vm.ThemeIndex = (int)AppTheme.Dark;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(repository.Saved.TorrentPrivacyNoticeAccepted);
        var loaded = new PreferencesViewModel(repository);
        await loaded.LoadAsync(default);
        Assert.True(loaded.TorrentPrivacyNoticeAccepted);
    }
    private sealed class ImmediatePreferences : IUserPreferencesRepository
    {
        public UserPreferences Saved { get; private set; } = new();
        public Task<UserPreferences> LoadAsync(CancellationToken token) => Task.FromResult(Saved);
        public Task SaveAsync(UserPreferences preferences, CancellationToken token)
        { Saved = preferences; return Task.CompletedTask; }
    }
    private sealed class ImmediateDispatcher : IUiDispatcher { public void Enqueue(Action action) => action(); }
    private sealed class MutableEngine : IAria2Client
    {
        public Dictionary<string, DownloadSnapshot> Rows { get; } = [];
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.FromResult<IReadOnlyList<EngineDownload>>(ids.Select(id => new EngineDownload(id, Rows[id])).ToArray());
        public Task SetConcurrencyAsync(int limit, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class BlockingPreferences : IUserPreferencesRepository
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UserPreferences Saved = new(); public int MaxConcurrent; private int _running; private bool _first = true;
        public Task<UserPreferences> LoadAsync(CancellationToken token) => Task.FromResult(Saved);
        public async Task SaveAsync(UserPreferences preferences, CancellationToken token)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _running));
            try { if (_first) { _first = false; Entered.TrySetResult(); await Release.Task; } Saved = preferences; }
            finally { Interlocked.Decrement(ref _running); }
        }
    }
    private static DownloadEntry Entry(DownloadStatus status)
    {
        var id = Guid.NewGuid(); var attempt = Guid.NewGuid(); var time = DateTimeOffset.UtcNow;
        return new(new(id, "file.bin", new("https://example.com/file.bin"), @"C:\Downloads\file.bin", time),
            new(attempt, id, "0000000000000001", time), new(Guid.NewGuid(), id, attempt, "file.bin", 100), new(status, 0, 100, 0));
    }
    [Fact]
    public async Task Batch_remove_is_confirmed_when_entry_disappears()
    {
        var row = Entry(DownloadStatus.Downloading);
        DownloadEntry? current = row;
        var results = await DownloadBatch.ExecuteAsync([row.Job.Id], DownloadAction.Remove,
            _ => current, () => true, (_, _, _) => { current = null; return Task.CompletedTask; }, default);
        Assert.Equal(BatchOutcome.Confirmed, Assert.Single(results).Outcome);
    }
    [Fact]
    public async Task Batch_rechecks_mixed_stale_and_disconnected_selection_and_deduplicates()
    {
        var active = Entry(DownloadStatus.Downloading); var paused = Entry(DownloadStatus.Paused); var late = Entry(DownloadStatus.Downloading);
        var rows = new[] { active, paused, late }.ToDictionary(row => row.Job.Id); var connected = true; var calls = 0;
        var results = await DownloadBatch.ExecuteAsync([active.Job.Id, active.Job.Id, paused.Job.Id, Guid.NewGuid(), late.Job.Id], DownloadAction.Pause,
            id => rows.GetValueOrDefault(id), () => connected, (id, _, _) =>
            { calls++; rows[id] = rows[id] with { Progress = new(DownloadStatus.Paused, 0, 100, 0) }; connected = false; return Task.CompletedTask; }, default);
        Assert.Equal(1, calls); Assert.Single(results, row => row.Outcome == BatchOutcome.Confirmed); Assert.Equal(3, results.Count(row => row.Outcome == BatchOutcome.Skipped));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_uncertain_intent_is_unconfirmed_even_after_failure_or_cancellation(bool canceled)
    {
        var row = Entry(DownloadStatus.Downloading);
        var result = await DownloadBatch.ExecuteAsync([row.Job.Id], DownloadAction.Pause, _ => row, () => true, (id, action, _) =>
        {
            row = row with { PendingOperation = new(Guid.NewGuid(), id, action, OperationStatus.Uncertain, DateTimeOffset.UtcNow) };
            return Task.FromException(canceled ? new OperationCanceledException() : new IOException());
        }, default);
        Assert.Equal(BatchOutcome.Unconfirmed, Assert.Single(result).Outcome);
    }
    [Fact]
    public async Task Cancellation_stops_submitting_remaining_items()
    {
        var rows = new[] { Entry(DownloadStatus.Downloading), Entry(DownloadStatus.Downloading) }.ToDictionary(row => row.Job.Id);
        using var cancellation = new CancellationTokenSource(); var calls = 0;
        var results = await DownloadBatch.ExecuteAsync(rows.Keys, DownloadAction.Pause, id => rows[id], () => true, (id, _, _) =>
        { calls++; rows[id] = rows[id] with { Progress = new(DownloadStatus.Paused, 0, 100, 0) }; cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token);
        Assert.Equal(1, calls); Assert.Equal(BatchOutcome.Canceled, results[1].Outcome);
    }
    [Fact]
    public void Layout_validation_rejects_nonfinite_duplicate_and_hidden_name()
    {
        var defaults = new DesktopLayout(); defaults.Validate();
        Assert.Throws<DownloadRequestException>(() => (defaults with { SidebarWidth = double.NaN }).Validate());
        Assert.Throws<DownloadRequestException>(() => (defaults with { DetailsHeight = double.PositiveInfinity }).Validate());
        Assert.Throws<DownloadRequestException>(() => (defaults with { DetailsWidth = 100 }).Validate());
        var normalized = (defaults with { SidebarWidth = 0, DetailsHeight = 0, DetailsWidth = 0 }).NormalizeSavedPaneSizes();
        Assert.Equal(120, normalized.SidebarWidth);
        Assert.Equal(120, normalized.DetailsHeight);
        Assert.Equal(280, normalized.DetailsWidth);
        Assert.Throws<DownloadRequestException>(() => (defaults with { Columns = defaults.EffectiveColumns.Select(c => c with { Column = DownloadSortColumn.Name }).ToArray() }).Validate());
        Assert.Throws<DownloadRequestException>(() => (defaults with { Columns = defaults.EffectiveColumns.Select(c => c.Column == DownloadSortColumn.Name ? c with { Visible = false } : c).ToArray() }).Validate());
    }
}
