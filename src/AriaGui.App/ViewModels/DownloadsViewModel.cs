using System.Collections.ObjectModel;
using AriaGui.App.Services;
using AriaGui.Application.Downloads;
using CommunityToolkit.Mvvm.Input;
using AriaGui.Core.Downloads;

namespace AriaGui.App.ViewModels;

public sealed class DownloadsViewModel : RuntimeViewModel
{
    private readonly DownloadCoordinator _coordinator;
    private readonly IUiDispatcher _dispatcher;
    private readonly DownloadRuntimeStore _runtimeStore;
    private readonly HashSet<Guid> _selectedIds = [];
    private DownloadSortColumn _sortColumn;
    private bool _sortDescending, _batchRunning;
    private string _batchMessage = "";
    private IReadOnlyList<DownloadBatchResult> _batchResults = [];
    private string _inputUrl = "";
    private string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AriaGUI");
    private string _fileName = "";
    private string _lastSuggestion = "";
    private bool _connected;
    private bool _unknownSize;
    private string _errorMessage = "";
    private string _connectionMessage = "Loading history…";
    private double _concurrencyLimit = 3;
    private string _queueMessage = "Waiting for the engine…";
    private string _completionMessage = "";
    private bool _policyPending;
    private bool _policyLoaded;
    private bool _isAddExpanded;
    private readonly IDownloadRepository? _repository;
    private readonly IDownloadFileActions? _fileActions;
    private readonly Dictionary<Guid, DownloadItemViewModel> _rows = [];
    private DownloadRuntimeSnapshot? _snapshot;
    private HistoryPage _history = new([], 0, 0, 50);
    private CancellationTokenSource? _browseCancellation;
    private Task _browseTask = Task.CompletedTask;
    private long _browseRevision;
    private bool _historyView, _browsing, _disposed;
    private string _search = "", _browserMessage = "Live downloads and up to 100 recent records. Browse history for older downloads.";
    private int _filter;
    private Guid? _lastCompletion;
    private readonly CancellationTokenSource _commandsLifetime = new();
    private readonly HashSet<Task> _operations = [];
    private readonly object _operationsSync = new();

    public DownloadsViewModel(DownloadCoordinator coordinator, DownloadRuntimeStore store, IUiDispatcher dispatcher,
        IDownloadFolderPicker? folderPicker = null, IDownloadRepository? repository = null, IDownloadFileActions? fileActions = null)
        : base(store, dispatcher)
    {
        _coordinator = coordinator;
        _runtimeStore = store;
        _dispatcher = dispatcher;
        _repository = repository; _fileActions = fileActions;
        BatchPauseCommand = BatchCommand(DownloadAction.Pause); BatchResumeCommand = BatchCommand(DownloadAction.Resume);
        BatchRemoveCommand = BatchCommand(DownloadAction.Remove); BatchRetryCommand = BatchCommand(DownloadAction.Retry); BatchRecoverCommand = BatchCommand(DownloadAction.Recover);
        PreviousPageCommand = new RelayCommand(() => QueueHistory(Math.Max(0, _history.Page - 1), false), () => IsHistoryView && !_browsing && _history.Page > 0);
        NextPageCommand = new RelayCommand(() => QueueHistory(_history.Page + 1, false), () => IsHistoryView && !_browsing && _history.HasNext);
        RefreshHistoryCommand = new RelayCommand(() => QueueHistory(_history.Page, false), () => IsHistoryView && !_browsing);
        ApplyLimitCommand = new AsyncRelayCommand(token => HandleAsync(() => _coordinator.SetConcurrencyAsync(ValidatedLimit(), token)),
            () => _connected && !_policyPending);
        AddDownloadCommand = new AsyncRelayCommand(AddDownloadAsync, () => _connected);
        BrowseCommand = new AsyncRelayCommand(async token =>
        {
            try
            {
                if (folderPicker is not null && await folderPicker.ChooseAsync(token) is { } path) DirectoryPath = path;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { ErrorMessage = "The folder picker could not open. You can type a destination folder instead."; }
        }, () => folderPicker is not null);
        Initialize();
    }

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];
    public IReadOnlyCollection<Guid> SelectedIds => _selectedIds.ToArray();
    public int SelectedCount => _selectedIds.Count;
    public DownloadItemViewModel? SelectedItem => Downloads.FirstOrDefault(row => _selectedIds.Contains(row.Id));
    public string BatchMessage { get => _batchMessage; private set => SetProperty(ref _batchMessage, value); }
    public IReadOnlyList<DownloadBatchResult> BatchResults => _batchResults;
    public bool IsBatchRunning { get => _batchRunning; private set { if (SetProperty(ref _batchRunning, value)) NotifyBatchCommands(); } }
    public IAsyncRelayCommand BatchPauseCommand { get; }
    public IAsyncRelayCommand BatchResumeCommand { get; }
    public IAsyncRelayCommand BatchRemoveCommand { get; }
    public IAsyncRelayCommand BatchRetryCommand { get; }
    public IAsyncRelayCommand BatchRecoverCommand { get; }
    private IEnumerable<IAsyncRelayCommand> BatchCommands => [BatchPauseCommand, BatchResumeCommand, BatchRemoveCommand, BatchRetryCommand, BatchRecoverCommand];
    public IReadOnlyDictionary<DownloadFilter, int> SidebarCounts => Enum.GetValues<DownloadFilter>().ToDictionary(filter => filter,
        filter => _snapshot?.Downloads.Count(row => DownloadBrowsing.Matches(row, "", filter)) ?? 0);
    public string SidebarCountScope => "Loaded live and up to 100 recent records";
    public DownloadSortColumn SortColumn { get => _sortColumn; set { if (Enum.IsDefined(value) && SetProperty(ref _sortColumn, value)) BrowseChanged(); } }
    public bool SortDescending { get => _sortDescending; set { if (SetProperty(ref _sortDescending, value)) BrowseChanged(); } }
    public void SetSort(DownloadSortColumn column) { if (SortColumn == column) SortDescending = !SortDescending; else { _sortDescending = false; SortColumn = column; } }
    public void SetSelection(IEnumerable<Guid> ids)
    {
        var visible = Downloads.Select(row => row.Id).ToHashSet(); _selectedIds.Clear(); _selectedIds.UnionWith(ids.Where(visible.Contains));
        OnPropertyChanged(nameof(SelectedIds)); OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(SelectedItem)); NotifyBatchCommands();
    }
    private void NotifyBatchCommands() { foreach (var command in BatchCommands) command.NotifyCanExecuteChanged(); }
    private IAsyncRelayCommand BatchCommand(DownloadAction action) => new AsyncRelayCommand(token => ExecuteBatchAsync(action, token),
        () => !IsBatchRunning && _selectedIds.Any(id => ResolveBatchEntry(id) is { } entry && DownloadBatch.CanExecute(entry, _connected, action)));
    private DownloadEntry? ResolveBatchEntry(Guid id) => _runtimeStore.Current.Downloads.FirstOrDefault(row => row.Job.Id == id)
        ?? _rows.GetValueOrDefault(id)?.Entry;
    private async Task ExecuteBatchAsync(DownloadAction action, CancellationToken token)
    {
        if (IsBatchRunning) return;
        var selection = _selectedIds.ToArray(); IsBatchRunning = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _commandsLifetime.Token);
        try
        {
            _batchResults = await DownloadBatch.ExecuteAsync(selection, action, ResolveBatchEntry,
                () => _runtimeStore.Current.Connection == EngineConnectionState.Connected, ExecuteCoordinatorAsync, linked.Token);
            _dispatcher.Enqueue(() =>
            {
                OnPropertyChanged(nameof(BatchResults));
                BatchMessage = $"{action}: {string.Join(" · ", Enum.GetValues<BatchOutcome>().Select(outcome => $"{_batchResults.Count(row => row.Outcome == outcome)} {outcome.ToString().ToLowerInvariant()}"))}";
                if (!_disposed && IsHistoryView && !IsBrowsing) QueueHistory(_history.Page, false);
            });
        }
        finally { _dispatcher.Enqueue(() => IsBatchRunning = false); }
    }
    public IAsyncRelayCommand AddDownloadCommand { get; }
    public IAsyncRelayCommand BrowseCommand { get; }
    public IAsyncRelayCommand ApplyLimitCommand { get; }
    public IRelayCommand PreviousPageCommand { get; }
    public IRelayCommand NextPageCommand { get; }
    public IRelayCommand RefreshHistoryCommand { get; }
    public bool IsHistoryView { get => _historyView; set { if (SetProperty(ref _historyView, value)) BrowseChanged(); } }
    public string SearchText { get => _search; set { if (SetProperty(ref _search, value.Length > 200 ? value[..200] : value)) BrowseChanged(); } }
    public int FilterIndex { get => _filter; set { if (value is >= 0 and <= 7 && SetProperty(ref _filter, value)) BrowseChanged(); } }
    public bool IsBrowsing { get => _browsing; private set { if (SetProperty(ref _browsing, value)) { NotifyPageCommands(); OnPropertyChanged(nameof(IsEmpty)); } } }
    public string BrowserMessage { get => _browserMessage; private set => SetProperty(ref _browserMessage, value); }
    public string EmptyMessage => SearchText.Length > 0 || FilterIndex != 0 ? "No downloads match this search and filter."
        : IsHistoryView ? "No completed, failed, or removed downloads in history yet." : "Add a URL above to start a download.";
    public string EmptyTitle => SearchText.Length > 0 || FilterIndex != 0 ? "No matching downloads" : IsHistoryView ? "No saved downloads yet" : "Your next download starts here";
    public Task BrowsingTask => _browseTask;
    private void NotifyPageCommands()
    { PreviousPageCommand.NotifyCanExecuteChanged(); NextPageCommand.NotifyCanExecuteChanged(); RefreshHistoryCommand.NotifyCanExecuteChanged(); }
    private void BrowseChanged()
    {
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(EmptyTitle));
        if (IsHistoryView) { _history = new([], 0, 0, 50); QueueHistory(0, true); }
        else { CancelBrowse(); _browseRevision++; IsBrowsing = false; BrowserMessage = "Live downloads and up to 100 recent records. Browse history for older downloads."; }
        Render(true); NotifyPageCommands();
    }
    private void QueueHistory(int page, bool debounce)
    {
        if (!IsHistoryView || _repository is null || _disposed) return;
        CancelBrowse();
        var cancellation = new CancellationTokenSource(); _browseCancellation = cancellation;
        var revision = ++_browseRevision; var query = new HistoryQuery(SearchText, (DownloadFilter)FilterIndex, page, SortColumn: SortColumn, SortDescending: SortDescending);
        IsBrowsing = true; BrowserMessage = "Loading history…";
        _browseTask = LoadHistoryAsync(query, revision, debounce, cancellation);
    }
    private async Task LoadHistoryAsync(HistoryQuery query, long revision, bool debounce, CancellationTokenSource cancellation)
    {
        try
        {
            if (debounce) await Task.Delay(250, cancellation.Token).ConfigureAwait(false);
            await _coordinator.InitializeAsync(cancellation.Token).ConfigureAwait(false);
            var page = await _repository!.LoadPageAsync(query, cancellation.Token).ConfigureAwait(false);
            if (page.Entries.Count == 0 && page.Total > 0 && page.Page > 0)
                page = await _repository.LoadPageAsync(query with { Page = (page.Total - 1) / query.PageSize }, cancellation.Token).ConfigureAwait(false);
            _dispatcher.Enqueue(() =>
            {
                if (_disposed || revision != _browseRevision) return;
                _history = page; IsBrowsing = false;
                BrowserMessage = $"Page {page.Page + 1} · {page.Entries.Count} shown · {page.Total} matching historical downloads";
                Render(); NotifyPageCommands();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            _dispatcher.Enqueue(() => { if (!_disposed && revision == _browseRevision) { IsBrowsing = false; BrowserMessage = "History could not be loaded. Check folder access and use Refresh history."; } });
        }
        finally { cancellation.Dispose(); }
    }
    public bool IsAddExpanded { get => _isAddExpanded; set => SetProperty(ref _isAddExpanded, value); }
    public double ConcurrencyLimit { get => _concurrencyLimit; set => SetProperty(ref _concurrencyLimit, value); }
    private int ValidatedLimit() => double.IsFinite(ConcurrencyLimit) && ConcurrencyLimit is >= 1 and <= 16 && ConcurrencyLimit == Math.Truncate(ConcurrencyLimit)
        ? (int)ConcurrencyLimit : throw new ArgumentException("Choose a whole-number limit from 1 to 16.");
    public string QueueMessage { get => _queueMessage; private set => SetProperty(ref _queueMessage, value); }
    public string CompletionMessage { get => _completionMessage; private set { if (SetProperty(ref _completionMessage, value)) OnPropertyChanged(nameof(HasCompletion)); } }
    public bool HasCompletion => CompletionMessage.Length > 0;
    public string InputUrl
    {
        get => _inputUrl;
        set
        {
            if (!SetProperty(ref _inputUrl, value)) return;
            if (Uri.TryCreate(value, UriKind.Absolute, out var source) && source.Scheme is "http" or "https")
            {
                var suggestion = Uri.UnescapeDataString(source.Segments.LastOrDefault() ?? "").Trim('/');
                if (string.IsNullOrWhiteSpace(suggestion)) suggestion = "download.bin";
                if (string.IsNullOrEmpty(FileName) || FileName == _lastSuggestion) FileName = suggestion;
                _lastSuggestion = suggestion;
            }
        }
    }
    public string DirectoryPath { get => _directory; set => SetProperty(ref _directory, value); }
    public string FileName { get => _fileName; set => SetProperty(ref _fileName, value); }
    public bool UnknownSize { get => _unknownSize; set => SetProperty(ref _unknownSize, value); }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
#if DEBUG
    // Called only by the explicitly marked synthetic UI fixture; no commands run.
    internal void SeedVerificationNotices(string batch, string error, string completion)
    {
        BatchMessage = batch;
        _batchResults = string.IsNullOrWhiteSpace(batch) ? [] : Enumerable.Range(0, 12)
            .Select(_ => new DownloadBatchResult(Guid.Empty, BatchOutcome.Skipped, batch)).ToArray();
        OnPropertyChanged(nameof(BatchResults));
        ErrorMessage = error;
        CompletionMessage = completion;
    }
#endif
    public bool IsEmpty => Downloads.Count == 0 && !IsBrowsing;
    public string ConnectionMessage { get => _connectionMessage; private set => SetProperty(ref _connectionMessage, value); }

    private async Task AddDownloadAsync(CancellationToken token)
    {
        ErrorMessage = "";
        try
        {
            await _coordinator.AddAsync(InputUrl, UnknownSize, token, DirectoryPath, FileName);
            _dispatcher.Enqueue(() => { InputUrl = ""; FileName = ""; IsAddExpanded = false; });
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException ex) { _dispatcher.Enqueue(() => ErrorMessage = ex.Message.Split(Environment.NewLine)[0]); }
        catch (DownloadRequestException ex) { _dispatcher.Enqueue(() => ErrorMessage = ex.Message); }
        catch (DownloadStorageException ex) { _dispatcher.Enqueue(() => ErrorMessage = ex.Message); }
        catch (Exception) { _dispatcher.Enqueue(() => ErrorMessage = "Add was not confirmed. Check the download row; its saved ID will be reconciled without resubmitting."); }
    }

    protected override void Apply(DownloadRuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        _connected = snapshot.Connection == EngineConnectionState.Connected;
        Render();
        OnPropertyChanged(nameof(SidebarCounts)); NotifyBatchCommands();
        AddDownloadCommand.NotifyCanExecuteChanged();
        var policy = snapshot.QueuePolicy ?? new();
        if (!_policyLoaded && snapshot.QueuePolicy is not null)
        { ConcurrencyLimit = policy.RequestedLimit ?? policy.Limit; _policyLoaded = true; }
        _policyPending = policy.RequestStatus is not null;
        ApplyLimitCommand.NotifyCanExecuteChanged();
        QueueMessage = policy.RequestStatus switch
        {
            OperationStatus.Pending => $"Applying limit {policy.RequestedLimit}…",
            OperationStatus.Uncertain => $"Limit {policy.RequestedLimit} is awaiting confirmation; observed {snapshot.ObservedConcurrency?.ToString() ?? "unknown"}.",
            _ => snapshot.ObservedConcurrency is int limit ? $"Engine limit: {limit}. Waiting order never preempts active downloads." : "Waiting for the engine…"
        };
        if (snapshot.LatestCompletion is { } notice && notice.AttemptId != _lastCompletion)
        { _lastCompletion = notice.AttemptId; CompletionMessage = $"{notice.FileName} finished downloading."; if (IsHistoryView && !IsBrowsing) QueueHistory(_history.Page, false); }
        ConnectionMessage = snapshot.Connection switch
        {
            EngineConnectionState.Connected => snapshot.ErrorMessage ?? "Connected · history saved locally",
            EngineConnectionState.Disconnected => snapshot.ErrorMessage ?? "Engine disconnected",
            _ => "Loading history and connecting…"
        };
    }
    private void Render(bool explicitBrowse = false)
    {
        if (_snapshot is null) return;
        var current = _snapshot.Downloads.ToDictionary(row => row.Job.Id);
        var source = IsHistoryView ? _history.Entries.Select(row => current.GetValueOrDefault(row.Job.Id) ?? row)
            .Where(row => DownloadBrowsing.IsTerminal(row.Progress.Status)) : _snapshot.Downloads;
        var filtered = source.Where(row => DownloadBrowsing.Matches(row, SearchText, (DownloadFilter)FilterIndex));
        var ordered = (IsHistoryView ? filtered : SortColumn == DownloadSortColumn.Default ? filtered
            .OrderBy(row => IsHistoryView ? 0 : row.Progress.Status switch { DownloadStatus.Downloading => 0, DownloadStatus.Queued => 1, DownloadStatus.Paused => 2, _ => 3 })
            .ThenBy(row => row.QueuePosition ?? int.MaxValue).ThenBy(row => row.Job.CreatedAt).ThenBy(row => row.Job.Id)
            : SortLive(filtered)).ToArray();
        // Progress changes update cells without moving rows while the user selects or acts.
        if (!explicitBrowse && !IsHistoryView && (_selectedIds.Count > 0 || IsBatchRunning))
        {
            var positions = Downloads.Select((row, index) => (row.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
            ordered = ordered.OrderBy(row => positions.GetValueOrDefault(row.Job.Id, int.MaxValue)).ToArray();
        }
        var visible = ordered.Select(row => row.Job.Id).ToHashSet();
        for (var i = Downloads.Count - 1; i >= 0; i--) if (!visible.Contains(Downloads[i].Id)) Downloads.RemoveAt(i);
        for (var index = 0; index < ordered.Length; index++)
        {
            var entry = ordered[index];
            if (!_rows.TryGetValue(entry.Job.Id, out var item))
            {
                item = new(entry, ExecuteAsync, (id, offset, token) => RunOperation(inner => _coordinator.MoveAsync(id, offset, inner), token),
                    _fileActions is null ? null : (path, folder, token) => RunOperation(inner => folder ? _fileActions.OpenFolderAsync(path, inner) : _fileActions.OpenFileAsync(path, inner), token));
                _rows[entry.Job.Id] = item;
            }
            item.Update(entry, _connected);
            if (index < Downloads.Count && ReferenceEquals(Downloads[index], item)) continue;
            var from = Downloads.IndexOf(item);
            if (from >= 0) Downloads.Move(from, index); else Downloads.Insert(index, item);
        }
        foreach (var id in _rows.Keys.Where(id => !visible.Contains(id) && !_rows[id].Commands.Any(command => command.IsRunning)).ToArray()) _rows.Remove(id);
        _selectedIds.IntersectWith(visible);
        OnPropertyChanged(nameof(SelectedIds)); OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(IsEmpty));
    }
    private IEnumerable<DownloadEntry> SortLive(IEnumerable<DownloadEntry> entries)
    {
        Func<DownloadEntry, IComparable?> key = SortColumn switch
        {
            DownloadSortColumn.Name => row => row.Job.DisplayName.ToUpperInvariant(),
            DownloadSortColumn.Size => row => row.Progress.TotalBytes,
            DownloadSortColumn.Progress => row => row.Progress.Percentage,
            DownloadSortColumn.Status => row => (int)row.Progress.Status,
            DownloadSortColumn.Speed => row => row.Progress.BytesPerSecond,
            DownloadSortColumn.Eta => row => row.Progress.EstimatedTimeRemaining?.Ticks,
            _ => row => row.Job.CreatedAt.UtcTicks
        };
        return (SortDescending ? entries.OrderByDescending(key) : entries.OrderBy(key)).ThenBy(row => row.Job.Id);
    }
    private Task ExecuteCoordinatorAsync(Guid id, DownloadAction action, CancellationToken token) => action switch
    {
        DownloadAction.Pause => _coordinator.PauseAsync(id, token), DownloadAction.Resume => _coordinator.ResumeAsync(id, token),
        DownloadAction.Remove => _coordinator.RemoveAsync(id, token), DownloadAction.Retry => _coordinator.RetryAsync(id, token),
        DownloadAction.Recover => _coordinator.RecoverAsync(id, token), _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    private Task ExecuteAsync(Guid id, DownloadAction action, CancellationToken token) => RunOperation(async inner =>
    {
        if (ResolveBatchEntry(id) is not { } entry || !DownloadBatch.CanExecute(entry, _runtimeStore.Current.Connection == EngineConnectionState.Connected, action))
            throw new DownloadRequestException("This download is no longer eligible for that command.");
        await ExecuteCoordinatorAsync(id, action, inner).ConfigureAwait(false);
        _dispatcher.Enqueue(() => { if (!_disposed && IsHistoryView && !IsBrowsing) QueueHistory(_history.Page, false); });
    }, token);
    private Task RunOperation(Func<CancellationToken, Task> work, CancellationToken token)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _commandsLifetime.Token);
        var task = HandleAsync(() => work(cancellation.Token));
        lock (_operationsSync) _operations.Add(task);
        return FinishAsync();
        async Task FinishAsync()
        {
            try { await task.ConfigureAwait(false); }
            finally { lock (_operationsSync) _operations.Remove(task); cancellation.Dispose(); }
        }
    }
    private async Task HandleAsync(Func<Task> work)
    {
        _dispatcher.Enqueue(() => ErrorMessage = "");
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (DownloadRequestException ex) { _dispatcher.Enqueue(() => ErrorMessage = ex.Message); }
        catch (DownloadStorageException ex) { _dispatcher.Enqueue(() => ErrorMessage = ex.Message); }
        catch (ArgumentException ex) { _dispatcher.Enqueue(() => ErrorMessage = ex.Message.Split(Environment.NewLine)[0]); }
        catch (Exception) { _dispatcher.Enqueue(() => ErrorMessage = "The command was not confirmed. Check the row and engine connection; its saved intent will be reconciled."); }
    }
    public IEnumerable<Task> PendingTasks => new[] { AddDownloadCommand, BrowseCommand, ApplyLimitCommand }
        .Concat(BatchCommands).Concat(_rows.Values.SelectMany(item => item.Commands)).Select(command => command.ExecutionTask).OfType<Task>().Concat(OperationTasks()).Append(_browseTask).ToArray();
    private Task[] OperationTasks() { lock (_operationsSync) return _operations.ToArray(); }
    public void CancelOperations()
    {
        CancelBrowse();
        _commandsLifetime.Cancel();
        foreach (var command in new[] { AddDownloadCommand, BrowseCommand, ApplyLimitCommand }.Concat(BatchCommands).Concat(_rows.Values.SelectMany(item => item.Commands)).ToArray()) command.Cancel();
    }
    public override void Dispose() { if (_disposed) return; _disposed = true; CancelOperations(); base.Dispose(); _commandsLifetime.Dispose(); }
    private void CancelBrowse()
    { try { _browseCancellation?.Cancel(); } catch (ObjectDisposedException) { } _browseCancellation = null; }
}
