using System.ComponentModel;
using AriaGui.Application.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AriaGui.App.Views.Downloads;

public sealed partial class FiltersRail : UserControl
{
    private static readonly string[] Labels =
        ["All", "Downloading", "Waiting", "Paused", "Interrupted", "Completed", "Failed", "Stopped"];
    private DownloadsPage? _page;
    private RailDestination _activeDestination = RailDestination.Downloads;
    private int _notificationUnreadCount;

    public FiltersRail()
    {
        InitializeComponent();
        Loaded += FiltersRailLoaded;
        DataContextChanged += (_, _) => AttachPage();
    }

    public ListView Filters => StatusFilters;
    public event RoutedEventHandler? SettingsRequested;
    public event RoutedEventHandler? NotificationsRequested;
    public event RoutedEventHandler? ViewChanged;
    public void UpdateSelection(bool history) => SetActiveDestination(history ? RailDestination.History : RailDestination.Downloads);

    private void SetActiveDestination(RailDestination destination)
    {
        _activeDestination = destination;
        SetDestinationButton(DownloadsButton, destination == RailDestination.Downloads);
        SetDestinationButton(HistoryButton, destination == RailDestination.History);
        SetDestinationButton(NotificationsButton, destination == RailDestination.Notifications);
        SetDestinationButton(SettingsButton, destination == RailDestination.Settings);
        TransferFilters.Visibility = destination == RailDestination.Downloads ? Visibility.Visible : Visibility.Collapsed;
        RefreshCounts();
        RefreshDestinationAccessibility();
    }

    private static void SetDestinationButton(ToggleButton button, bool selected)
    {
        button.IsChecked = selected;
        button.ApplyTemplate();
        VisualStateManager.GoToState(button, selected ? "Checked" : "Unchecked", false);
        AutomationProperties.SetItemStatus(button, selected ? "Current destination" : string.Empty);
    }

    public void SetSettingsSelected(bool selected)
    {
        SetActiveDestination(selected ? RailDestination.Settings
            : _page?.ViewModel.IsHistoryView == true ? RailDestination.History : RailDestination.Downloads);
    }

    public void SetNotificationsSelected(bool selected)
    {
        SetActiveDestination(selected ? RailDestination.Notifications
            : _page?.ViewModel.IsHistoryView == true ? RailDestination.History : RailDestination.Downloads);
    }
    public void SetNotificationUnreadCount(int count)
    {
        _notificationUnreadCount = count;
        NotificationsCount.Text = count > 0 ? FormatCount(count) : string.Empty;
        RefreshDestinationAccessibility();
    }
    private void DownloadsClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is DownloadsPage page) page.ViewModel.IsHistoryView = false;
        UpdateSelection(false); ViewChanged?.Invoke(this, args);
    }
    private void HistoryClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is DownloadsPage page) page.ViewModel.IsHistoryView = true;
        UpdateSelection(true); ViewChanged?.Invoke(this, args);
    }
    private void SettingsClick(object sender, RoutedEventArgs args) => SettingsRequested?.Invoke(this, args);
    private void NotificationsClick(object sender, RoutedEventArgs args) => NotificationsRequested?.Invoke(this, args);

    private enum RailDestination { Downloads, History, Notifications, Settings }

    private void FiltersRailLoaded(object sender, RoutedEventArgs args)
    {
        AttachPage();
        SetActiveDestination(_activeDestination);
    }

    private void RailLayoutLoaded(object sender, RoutedEventArgs args) => UpdateScrollLimits();

    private void RailLayoutSizeChanged(object sender, SizeChangedEventArgs args) => UpdateScrollLimits();

    private void UpdateScrollLimits()
    {
        if (RailLayout.ActualHeight <= 0 || RailHeader.ActualHeight <= 0) return;
        var outerPadding = (Thickness)Microsoft.UI.Xaml.Application.Current.Resources["AriaSpace2"];
        var minimumFilterViewport = (double)Microsoft.UI.Xaml.Application.Current.Resources["AriaRowHeightCompact"];
        var contentHeight = Math.Max(0, RailLayout.ActualHeight - outerPadding.Top - outerPadding.Bottom);
        var navigationViewport = (double)Microsoft.UI.Xaml.Application.Current.Resources["AriaControlTargetSize"];
        NavigationScroller.MaxHeight = Math.Max(navigationViewport, contentHeight - RailHeader.ActualHeight - minimumFilterViewport);
    }

    private void AttachPage()
    {
        var page = DataContext as DownloadsPage;
        if (ReferenceEquals(page, _page)) { RefreshCounts(); return; }
        if (_page is not null) _page.ViewModel.PropertyChanged -= ViewModelChanged;
        _page = page;
        if (_page is not null) _page.ViewModel.PropertyChanged += ViewModelChanged;
        RefreshCounts();
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AriaGui.App.ViewModels.DownloadsViewModel.SidebarCounts)
            or nameof(AriaGui.App.ViewModels.DownloadsViewModel.IsHistoryView)
            or nameof(AriaGui.App.ViewModels.DownloadsViewModel.FilterIndex))
            RefreshCounts();
    }

    public void RefreshCounts()
    {
        if (_page is null) return;
        var counts = _page.ViewModel.SidebarCounts;
        for (var index = 0; index < StatusFilters.Items.Count && index < Labels.Length; index++)
        {
            if (StatusFilters.Items[index] is not Grid row) continue;
            var count = row.Children.OfType<TextBlock>().FirstOrDefault(text => Equals(text.Tag, "FilterCount"));
            if (count is null) continue;
            var value = counts.GetValueOrDefault((DownloadFilter)index);
            count.Text = FormatCount(value);
            AutomationProperties.SetName(row, $"{Labels[index]}, {value} transfers");
        }
        var history = counts.GetValueOrDefault(DownloadFilter.Completed)
            + counts.GetValueOrDefault(DownloadFilter.Failed)
            + counts.GetValueOrDefault(DownloadFilter.Removed);
        HistoryCount.Text = FormatCount(history);
        RefreshDestinationAccessibility();
    }

    private void RefreshDestinationAccessibility()
    {
        var counts = _page?.ViewModel.SidebarCounts;
        var history = counts is null ? 0
            : counts.GetValueOrDefault(DownloadFilter.Completed)
                + counts.GetValueOrDefault(DownloadFilter.Failed)
                + counts.GetValueOrDefault(DownloadFilter.Removed);
        AutomationProperties.SetName(DownloadsButton, DestinationName("Downloads", RailDestination.Downloads));
        AutomationProperties.SetName(HistoryButton, DestinationName($"History, {FormatCount(history)} records", RailDestination.History));
        AutomationProperties.SetName(NotificationsButton, DestinationName(
            _notificationUnreadCount > 0 ? $"Notifications, {FormatCount(_notificationUnreadCount)} unread" : "Notifications",
            RailDestination.Notifications));
        AutomationProperties.SetName(SettingsButton, DestinationName("Settings", RailDestination.Settings));

        string DestinationName(string name, RailDestination destination) =>
            destination == _activeDestination ? $"{name}, current destination" : name;
    }

    private static string FormatCount(int count) => count > 999 ? "999+" : count.ToString();
}
