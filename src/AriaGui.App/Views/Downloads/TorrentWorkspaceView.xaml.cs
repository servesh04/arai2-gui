using AriaGui.Core.Downloads;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Downloads;

public sealed partial class TorrentWorkspaceView : UserControl
{
    private SqliteTorrentMetadataRepository? _repository;
    private Aria2ProcessManager? _engine;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<Guid, TorrentEngineObservation> _observations = [];
    private bool _refreshing;
    private bool _active;
    private Guid? _selectedId;
    private IReadOnlyList<TorrentRow> _allRows = [];
    private int _filterIndex;

    public int FilterIndex
    {
        get => _filterIndex;
        set { if (value is >= 0 and <= 7 && _filterIndex != value) { _filterIndex = value; ApplyFilter(); } }
    }
    public IReadOnlyDictionary<DownloadFilter, int> Counts => Enum.GetValues<DownloadFilter>()
        .ToDictionary(filter => filter, filter => filter == DownloadFilter.All
            ? _allRows.Count : _allRows.Count(row => row.Filter == filter));

    public event EventHandler? AddRequested;
    public event EventHandler? DownloadsRequested;
    public event EventHandler? CountsChanged;

    public TorrentWorkspaceView()
    {
        InitializeComponent();
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    public void Initialize(SqliteTorrentMetadataRepository repository, Aria2ProcessManager engine)
    { _repository = repository; _engine = engine; }

    public async void SetActive(bool active)
    {
        _active = active;
        if (!active) { _refreshTimer.Stop(); return; }
        _refreshTimer.Start();
        await RefreshAsync();
    }

    public async Task SelectAsync(Guid jobId)
    {
        _selectedId = jobId;
        await RefreshAsync();
    }

    public void ShowOperationError(string message)
    {
        OperationNotice.Message = message;
        OperationNotice.IsOpen = true;
        DetailsPanel.Visibility = Visibility.Visible;
    }

    private async Task RefreshAsync()
    {
        if (!_active || _refreshing || _repository is null || _engine is null) return;
        _refreshing = true;
        try
        {
            await _repository.InitializeAsync();
            var ids = await _repository.LoadDraftIdsAsync();
            var rows = new List<TorrentRow>();
            StoredTorrentDraft? selectedDraft = null;
            foreach (var id in ids)
            {
                var draft = await _repository.LoadDraftAsync(id);
                if (draft is null) continue;
                if (draft.Stage is not (TorrentStage.Completed or TorrentStage.Stopped or TorrentStage.Failed or TorrentStage.AwaitingSelection))
                {
                    var progressActive = true;
                    try
                    {
                        IProgress<TorrentPublicationProgress>? progress = null;
                        if (id == _selectedId)
                        {
                            ShowSelected(draft);
                            progress = new Progress<TorrentPublicationProgress>(value =>
                            {
                                if (!progressActive || !_active || _selectedId != value.JobId) return;
                                TorrentDetails.ShowPublicationProgress(value);
                            });
                        }
                        var observed = await _engine.ObserveTorrentAsync(id, CancellationToken.None, progress);
                        progressActive = false;
                        if (observed is not null) _observations[id] = observed;
                        else _observations.Remove(id);
                        draft = await _repository.LoadDraftAsync(id) ?? draft;
                        if (id == _selectedId && observed?.Stage == TorrentStage.Completed) OperationNotice.IsOpen = false;
                    }
                    catch (Exception ex) when (ex is EngineException or DownloadStorageException or IOException)
                    {
                        progressActive = false;
                        draft = await _repository.LoadDraftAsync(id) ?? draft;
                        if (id == _selectedId)
                        {
                            OperationNotice.Message = ex.Message;
                            OperationNotice.IsOpen = true;
                        }
                    }
                }
                rows.Add(new TorrentRow(id, draft.Manifest.Name, StageLabel(draft.Stage), StageFilter(draft.Stage)));
                if (id == _selectedId) selectedDraft = draft;
            }
            _allRows = rows;
            ApplyFilter();
            CountsChanged?.Invoke(this, EventArgs.Empty);
            if (selectedDraft is not null && _selectedId is { } selectedId
                && !rows.Any(row => row.Id == selectedId && (_filterIndex == 0 || (int)row.Filter == _filterIndex)))
                selectedDraft = null;
            if (selectedDraft is null)
            {
                DetailsPanel.Visibility = Visibility.Collapsed;
                SelectionMessage.Visibility = Visibility.Visible;
            }
            else ShowSelected(selectedDraft);
        }
        catch (Exception ex) when (ex is DownloadStorageException or IOException)
        {
            OperationNotice.Message = "Torrent history could not be loaded. " + ex.Message;
            OperationNotice.IsOpen = true;
            DetailsPanel.Visibility = Visibility.Visible;
        }
        finally { _refreshing = false; }
    }

    private void ApplyFilter()
    {
        if (TorrentList is null) return;
        var shown = _filterIndex == 0 ? _allRows : _allRows.Where(row => (int)row.Filter == _filterIndex).ToArray();
        TorrentList.ItemsSource = shown;
        EmptyMessage.Text = _allRows.Count == 0 ? "No torrents yet. Add a local .torrent file to choose its files and destination."
            : "No torrents match this status filter.";
        EmptyMessage.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TorrentList.SelectedItem = _selectedId is { } id ? shown.FirstOrDefault(row => row.Id == id) : null;
        if (TorrentList.SelectedItem is null)
        {
            DetailsPanel.Visibility = Visibility.Collapsed;
            SelectionMessage.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static DownloadFilter StageFilter(TorrentStage stage) => stage switch
    {
        TorrentStage.AwaitingSelection or TorrentStage.Waiting => DownloadFilter.Waiting,
        TorrentStage.Checking or TorrentStage.Downloading or TorrentStage.Verifying or TorrentStage.Publishing => DownloadFilter.Active,
        TorrentStage.Completed => DownloadFilter.Completed,
        TorrentStage.Failed => DownloadFilter.Failed,
        TorrentStage.Stopped => DownloadFilter.Removed,
        _ => DownloadFilter.Recovery
    };

    private void ShowSelected(StoredTorrentDraft draft)
    {
        SelectionMessage.Visibility = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Visible;
        _observations.TryGetValue(draft.JobId, out var observed);
        TorrentDetails.Show(draft, observed);
        DispatchButton.Content = draft.Stage == TorrentStage.Failed ? "Retry torrent" : "Start torrent";
        DispatchButton.Visibility = draft.Stage is TorrentStage.AwaitingSelection or TorrentStage.Failed
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string StageLabel(TorrentStage stage) => stage switch
    {
        TorrentStage.AwaitingSelection => "Ready to start",
        TorrentStage.Waiting => "Waiting",
        TorrentStage.Checking => "Checking files",
        TorrentStage.Downloading => "Downloading",
        TorrentStage.Verifying => "Verifying",
        TorrentStage.Publishing => "Publishing files",
        TorrentStage.Completed => "Completed",
        TorrentStage.Failed => "Needs review",
        TorrentStage.Stopped => "Stopped",
        _ => "Torrent"
    };

    private void TorrentSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (TorrentList.SelectedItem is not TorrentRow row) return;
        _selectedId = row.Id;
        _ = ShowSelectionAsync(row.Id);
    }

    private async Task ShowSelectionAsync(Guid id)
    {
        if (_repository is null) return;
        var draft = await _repository.LoadDraftAsync(id);
        if (draft is not null && _selectedId == id) ShowSelected(draft);
    }

    private async void DispatchClick(object sender, RoutedEventArgs args)
    {
        if (_selectedId is not { } id || _engine is null) return;
        DispatchButton.IsEnabled = false;
        OperationNotice.IsOpen = false;
        try
        {
            var draft = await _repository!.LoadDraftAsync(id);
            _observations.Remove(id);
            var result = draft?.Stage == TorrentStage.Failed
                ? await _engine.RetryFailedTorrentAsync(id, CancellationToken.None)
                : await _engine.DispatchTorrentAsync(id, CancellationToken.None);
            _observations[id] = result;
        }
        catch (Exception ex) when (ex is EngineException or DownloadStorageException or IOException)
        { OperationNotice.Message = ex.Message; OperationNotice.IsOpen = true; }
        finally { DispatchButton.IsEnabled = true; await RefreshAsync(); }
    }

    private void AddClick(object sender, RoutedEventArgs args) => AddRequested?.Invoke(this, EventArgs.Empty);
    private void DownloadsClick(object sender, RoutedEventArgs args) => DownloadsRequested?.Invoke(this, EventArgs.Empty);

    private sealed record TorrentRow(Guid Id, string Name, string Stage, DownloadFilter Filter);
}
