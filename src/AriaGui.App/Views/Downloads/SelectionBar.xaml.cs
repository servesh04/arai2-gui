using System.Collections.Specialized;
using System.ComponentModel;
using AriaGui.App.ViewModels;
using AriaGui.Core.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AriaGui.App.Views.Downloads;

public sealed partial class SelectionBar : UserControl
{
    private DownloadsPage? _page;
    private readonly HashSet<DownloadItemViewModel> _observedItems = [];
    private bool _hasRetry;

    public SelectionBar()
    {
        InitializeComponent();
        Loaded += SelectionBarLoaded;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateToolbarReplacement());
        LayoutUpdated += (_, _) =>
        {
            if (!_hasRetry)
            {
                RetryButton.Visibility = Visibility.Collapsed;
                OverflowRetry.Visibility = Visibility.Collapsed;
            }
        };
    }

    public StackPanel Actions => SelectedActions;
    public Button Retry => RetryButton;
    public MenuFlyout Menu => MoreMenu;
    public MenuFlyoutItem OverflowRetryItem => OverflowRetry;
    public event RoutedEventHandler? SingleActionRequested;
    private void SingleActionClick(object sender, RoutedEventArgs args) => SingleActionRequested?.Invoke(sender, args);

    private void SelectionBarLoaded(object sender, RoutedEventArgs args)
    {
        _page ??= FindAncestor<DownloadsPage>(this);
        if (_page is null) return;
        OverflowRetry.Command = _page.ViewModel.BatchRetryCommand;
        RecoverItem.Command = _page.ViewModel.BatchRecoverCommand;
        RestartFreshItem.Command = _page.ViewModel.BatchRestartFreshCommand;
        RemoveItem.Command = _page.ViewModel.BatchRemoveCommand;
        _page.ViewModel.PropertyChanged -= ViewModelChanged;
        _page.ViewModel.PropertyChanged += ViewModelChanged;
        _page.ViewModel.Downloads.CollectionChanged -= DownloadsChanged;
        _page.ViewModel.Downloads.CollectionChanged += DownloadsChanged;
        SyncObservedItems();
        Refresh();
        UpdateToolbarReplacement();
    }

    private void DownloadsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        SyncObservedItems();
        Refresh();
    }

    private void SyncObservedItems()
    {
        if (_page is null) return;
        var current = _page.ViewModel.Downloads.ToHashSet();
        foreach (var item in _observedItems.Where(item => !current.Contains(item)).ToArray())
        {
            item.PropertyChanged -= ItemChanged;
            _observedItems.Remove(item);
        }
        foreach (var item in current.Where(item => !_observedItems.Contains(item)))
        {
            item.PropertyChanged += ItemChanged;
            _observedItems.Add(item);
        }
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DownloadsViewModel.SelectedIds) or nameof(DownloadsViewModel.SelectedCount)
            or nameof(DownloadsViewModel.SelectedItem) or nameof(DownloadsViewModel.IsBatchRunning))
            Refresh();
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DownloadItemViewModel.RawStatus) or nameof(DownloadItemViewModel.Entry)
            or nameof(DownloadItemViewModel.ShowPause) or nameof(DownloadItemViewModel.ShowResume)
            or nameof(DownloadItemViewModel.ShowRetry) or nameof(DownloadItemViewModel.ShowRecover))
            Refresh();
    }

    private void Refresh()
    {
        if (_page is null) return;
        var selectedIds = _page.ViewModel.SelectedIds.ToHashSet();
        var selected = _page.ViewModel.Downloads.Where(item => selectedIds.Contains(item.Id)).ToArray();
        SelectedCountText.Text = $"{selected.Length} selected";
        PauseButton.Visibility = selected.Any(item => item.ShowPause) ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.Visibility = selected.Any(item => item.ShowResume) ? Visibility.Visible : Visibility.Collapsed;
        _hasRetry = selected.Any(item => item.ShowRetry);
        if (!_hasRetry)
        {
            RetryButton.Visibility = Visibility.Collapsed;
            OverflowRetry.Visibility = Visibility.Collapsed;
        }
        RecoverItem.Visibility = selected.Any(item => item.ShowRecover) ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Visibility = selected.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearClick(object sender, RoutedEventArgs args)
    {
        if (_page is null) return;
        if (FindDescendant<DownloadsTable>(_page) is { } table) table.List.SelectedItems.Clear();
    }

    private void UpdateToolbarReplacement()
    {
        _page ??= FindAncestor<DownloadsPage>(this);
        if (_page is null) return;
        if (FindDescendant<DownloadsToolbar>(_page) is { } toolbar)
            toolbar.Visibility = Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}
