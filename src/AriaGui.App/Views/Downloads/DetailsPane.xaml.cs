using System.Globalization;
using AriaGui.App.ViewModels;
using AriaGui.App.Views;
using AriaGui.Core.Downloads;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;

namespace AriaGui.App.Views.Downloads;

public sealed partial class DetailsPane : UserControl
{
    private MenuFlyout? _openMenu;
    private SqliteTorrentMetadataRepository? _torrentRepository;
    private Aria2ProcessManager? _torrentEngine;
    private readonly DispatcherTimer _fileRefreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DownloadItemViewModel? _selectedTorrent;
    private bool _refreshingFiles;
    private TorrentPublicationProgress? _latestPublication;
    private Storyboard? _contentEntranceStoryboard;

    public DetailsPane()
    {
        InitializeComponent();
        DetailsTabs.TabItems.Remove(TorrentFilesTab);
        Inspector.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            CloseOpenFlyouts();
            if (Inspector.Visibility != Visibility.Visible) CancelContentEntrance();
        });
        DetailsHost.DataContextChanged += (_, _) => { CloseOpenFlyouts(); SelectionChanged(); };
        _fileRefreshTimer.Tick += async (_, _) => await RefreshTorrentFilesAsync();
        Loaded += (_, _) => SubscribePublication();
        Unloaded += (_, _) => { CloseOpenFlyouts(); _fileRefreshTimer.Stop(); UnsubscribePublication(); };
    }

    public Grid Root => Inspector;
    public ScrollViewer Host => DetailsHost;
    public TextBlock Hint => SelectionHint;
    public StackPanel Metadata => InspectorMetadata;
    public Button OpenFile => InspectorOpenFile;
    public Button OpenFolder => InspectorOpenFolder;
    public event RoutedEventHandler? SingleActionRequested;

    public void AnimateContentEntrance(bool appAnimationsEnabled)
    {
        CancelContentEntrance();
        if (Inspector.Visibility != Visibility.Visible || DetailsHost.Visibility != Visibility.Visible
            || !UiMotion.IsEnabled(appAnimationsEnabled, XamlRoot))
        {
            DetailsHost.Opacity = 1;
            return;
        }
        DetailsHost.Opacity = 0;
        _contentEntranceStoryboard = UiMotion.Fade(
            DetailsHost, 0, 1, UiMotion.Duration("AriaMotionInspectorMilliseconds"),
            () =>
            {
                DetailsHost.Opacity = 1;
                _contentEntranceStoryboard = null;
            });
    }

    public void CancelContentEntrance()
    {
        _contentEntranceStoryboard?.Stop();
        _contentEntranceStoryboard = null;
        if (DetailsHost is not null) DetailsHost.Opacity = 1;
    }

    public void Initialize(SqliteTorrentMetadataRepository repository, Aria2ProcessManager engine)
    {
        _torrentRepository = repository;
        _torrentEngine = engine;
        if (IsLoaded) SubscribePublication();
    }

    private void SubscribePublication()
    {
        if (_torrentEngine is null) return;
        _torrentEngine.TorrentPublicationProgressChanged -= TorrentPublicationChanged;
        _torrentEngine.TorrentPublicationProgressChanged += TorrentPublicationChanged;
    }

    private void UnsubscribePublication()
    {
        if (_torrentEngine is not null)
            _torrentEngine.TorrentPublicationProgressChanged -= TorrentPublicationChanged;
    }

    private void TorrentPublicationChanged(object? sender, TorrentPublicationProgress progress) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_selectedTorrent?.Id != progress.JobId) return;
            _latestPublication = progress;
            RefreshActivity();
        });

    private void SelectionChanged()
    {
        if (_selectedTorrent is not null) _selectedTorrent.PropertyChanged -= SelectedTorrentChanged;
        _selectedTorrent = DetailsHost.DataContext as DownloadItemViewModel;
        if (_selectedTorrent is not null) _selectedTorrent.PropertyChanged += SelectedTorrentChanged;
        var torrent = _selectedTorrent?.Kind == TransferKind.Torrent;
        _latestPublication = torrent ? _torrentEngine?.GetTorrentPublicationProgress(_selectedTorrent!.Id) : null;
        if (!torrent && ReferenceEquals(DetailsTabs.SelectedItem, TorrentFilesTab)) DetailsTabs.SelectedIndex = 0;
        if (torrent && !DetailsTabs.TabItems.Contains(TorrentFilesTab)) DetailsTabs.TabItems.Add(TorrentFilesTab);
        if (!torrent && DetailsTabs.TabItems.Contains(TorrentFilesTab)) DetailsTabs.TabItems.Remove(TorrentFilesTab);
        TorrentFiles.ItemsSource = null;
        RefreshActivity();
        if (torrent) { _fileRefreshTimer.Start(); _ = RefreshTorrentFilesAsync(); }
        else _fileRefreshTimer.Stop();
    }

    private void SelectedTorrentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DownloadItemViewModel.SubstateText) or nameof(DownloadItemViewModel.Kind)
            or nameof(DownloadItemViewModel.SizeLabel) or nameof(DownloadItemViewModel.TorrentStage)
            or nameof(DownloadItemViewModel.TorrentVerifiedBytes) or nameof(DownloadItemViewModel.RawStatus)
            or nameof(DownloadItemViewModel.Progress))
            RefreshActivity();
    }

    private void RefreshActivity()
    {
        var item = _selectedTorrent;
        var substate = item?.SubstateText;
        if (item?.RawStatus is DownloadStatus.Completed or DownloadStatus.Failed) _latestPublication = null;
        var publishing = _latestPublication is { } current && current.JobId == item?.Id ? current : null;
        TorrentActivity.Visibility = item?.Kind == TransferKind.Torrent && (!string.IsNullOrWhiteSpace(substate) || publishing is not null)
            ? Visibility.Visible : Visibility.Collapsed;
        if (item is null) return;
        if (publishing is not null)
        {
            TorrentActivityLabel.Text = "Publishing files";
            TorrentActivityProgress.IsIndeterminate = false;
            TorrentActivityProgress.Value = publishing.TotalBytes == 0 ? 100
                : 100d * publishing.PublishedBytes / publishing.TotalBytes;
            TorrentActivityProgressText.Text = $"Published {FormatBytes(publishing.PublishedBytes)} of {FormatBytes(publishing.TotalBytes)}";
        }
        else if (item.TorrentStage == TorrentStage.Checking && item.TorrentVerifiedBytes is long verified
            && item.TorrentTotalBytes is long total && total > 0)
        {
            TorrentActivityLabel.Text = substate ?? "Checking files";
            TorrentActivityProgress.IsIndeterminate = false;
            TorrentActivityProgress.Value = 100d * Math.Min(verified, total) / total;
            TorrentActivityProgressText.Text = $"Checked {FormatBytes(verified)} of {FormatBytes(total)}";
        }
        else
        {
            TorrentActivityLabel.Text = substate ?? "";
            TorrentActivityProgress.IsIndeterminate = substate is "Verifying" ||
                (substate?.StartsWith("Publishing", StringComparison.Ordinal) ?? false);
            TorrentActivityProgress.Value = item.Progress;
            TorrentActivityProgressText.Text = item.SizeLabel;
        }
    }

    private async Task RefreshTorrentFilesAsync()
    {
        if (_refreshingFiles || _torrentRepository is null || _torrentEngine is null
            || _selectedTorrent is not { Kind: TransferKind.Torrent } item) return;
        _refreshingFiles = true;
        try
        {
            await _torrentRepository.InitializeAsync();
            var draft = await _torrentRepository.LoadDraftAsync(item.Id);
            if (draft is null || _selectedTorrent?.Id != item.Id) return;
            var observed = draft.Stage == TorrentStage.Completed
                ? null : await _torrentEngine.ObserveTorrentAsync(item.Id, CancellationToken.None);
            if (_selectedTorrent?.Id != item.Id) return;
            TorrentFiles.ItemsSource = draft.Manifest.Files.Select(file =>
            {
                var state = observed?.Files.FirstOrDefault(entry => entry.Index == file.Index);
                var selected = draft.Selection.FileIndices.Contains(file.Index);
                var completed = draft.Stage == TorrentStage.Completed && selected ? file.Length
                    : state?.CompletedLength ?? 0;
                return new TorrentDetailFileRow(file.RelativePath, file.Length, completed, selected);
            }).ToArray();
        }
        catch (Exception ex) when (ex is EngineException or DownloadStorageException or IOException or InvalidDataException)
        { TorrentFiles.ItemsSource = new[] { new TorrentDetailFileRow("File progress is temporarily unavailable.", 0, 0, false) }; }
        finally { _refreshingFiles = false; }
    }

    private static string FormatBytes(long bytes) => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824d:0.#} GiB"
        : bytes >= 1_048_576 ? $"{bytes / 1_048_576d:0.#} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes} B";

    private void SingleActionClick(object sender, RoutedEventArgs args) => SingleActionRequested?.Invoke(sender, args);

    private void CopyUrlClick(object sender, RoutedEventArgs args)
    {
        if (DetailsHost.DataContext is DownloadItemViewModel item) CopyText(item.SourceUrl);
    }

    private void CopyPathClick(object sender, RoutedEventArgs args)
    {
        if (DetailsHost.DataContext is DownloadItemViewModel item) CopyText(item.Destination);
    }

    private static void CopyText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
    }

    private void DetailsMenuOpening(object sender, object args)
    {
        _openMenu = (MenuFlyout)sender;
        DownloadFlyoutCoordinator.Showing(_openMenu);
    }

    private void DetailsMenuClosed(object sender, object args)
    {
        DownloadFlyoutCoordinator.Closed((FlyoutBase)sender);
        if (ReferenceEquals(_openMenu, sender)) _openMenu = null;
    }

    private async void RemoveClick(object sender, RoutedEventArgs args)
    {
        if (DetailsHost.DataContext is not DownloadItemViewModel item || !item.RemoveCommand.CanExecute(null)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove download?",
            Content = "The entry will be removed from the transfer list. Downloaded files are kept.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && item.RemoveCommand.CanExecute(null))
            item.RemoveCommand.Execute(null);
    }

    private async void CloseClick(object sender, RoutedEventArgs args)
    {
        CloseOpenFlyouts();
        if (DataContext is not DownloadsPage page) return;
        page.CloseDetailsFromPane();
        await page.SaveLayoutAsync();
    }

    private void CloseOpenFlyouts()
    {
        _openMenu?.Hide();
        _openMenu = null;
        DownloadFlyoutCoordinator.HideAll();
        if (FindDescendant<DownloadsTable>(FindAncestor<DownloadsPage>(this))?.List is not { } list) return;
        foreach (var item in list.Items)
            if (list.ContainerFromItem(item) is ListViewItem container) container.ContextFlyout?.Hide();
    }

    private void DetailsHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var viewportWidth = DetailsHost.ViewportWidth > 0 ? DetailsHost.ViewportWidth : args.NewSize.Width;
        DetailsContent.Width = viewportWidth;
        var columns = Inspector.ActualWidth >= 900 ? 4 : Inspector.ActualWidth >= 420 ? 2 : 1;
        if (OverviewCards.ColumnDefinitions.Count != columns)
        {
            OverviewCards.ColumnDefinitions.Clear();
            for (var index = 0; index < columns; index++)
                OverviewCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var cards = new[] { TransferCard, ProgressCard, NetworkCard, GeneralCard };
        for (var index = 0; index < cards.Length; index++)
        {
            Grid.SetColumn(cards[index], index % columns);
            Grid.SetRow(cards[index], index / columns);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}

public sealed record TorrentDetailFileRow(string RelativePath, long Length, long CompletedLength, bool Selected)
{
    public string SizeLabel => DetailsPaneFileFormat.Format(Length);
    public double Progress => Length == 0 ? 100 : 100d * Math.Min(CompletedLength, Length) / Length;
    public string ProgressLabel => $"{Progress:0}%";
}

internal static class DetailsPaneFileFormat
{
    public static string Format(long bytes) => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824d:0.#} GiB"
        : bytes >= 1_048_576 ? $"{bytes / 1_048_576d:0.#} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes} B";
}

public sealed class DetailsFileGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new DownloadFileGlyphConverter().Convert(value, targetType, parameter, language);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class ProtocolLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (!Uri.TryCreate(value as string, UriKind.Absolute, out var uri)) return "URL";
        return uri.Scheme.ToUpperInvariant();
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class FailureCardVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DownloadStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class FailureReasonVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string reason && !string.IsNullOrWhiteSpace(reason) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class HttpStatusVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class HttpStatusLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int status ? $"HTTP {status}" : string.Empty;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DownloadTypeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var extension = Path.GetExtension(value as string ?? string.Empty);
        return string.IsNullOrWhiteSpace(extension) ? "File" : $"{extension.TrimStart('.').ToUpperInvariant()} file";
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DetailsDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        DateTimeOffset date => date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        _ => "—"
    };
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
