using System.ComponentModel;
using AriaGui.Application.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Downloads;

public sealed partial class FiltersRail : UserControl
{
    private static readonly string[] Labels =
        ["All", "Downloading", "Waiting", "Paused", "Interrupted", "Completed", "Failed", "Stopped"];
    private DownloadsPage? _page;

    public FiltersRail()
    {
        InitializeComponent();
        Loaded += FiltersRailLoaded;
        DataContextChanged += (_, _) => AttachPage();
    }

    public ListView Filters => StatusFilters;
    public event RoutedEventHandler? SettingsRequested;
    public event RoutedEventHandler? ViewChanged;
    public void UpdateSelection(bool history)
    {
        TransferFilters.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        DownloadsButton.IsChecked = !history;
        HistoryButton.IsChecked = history;
        SettingsButton.IsChecked = false;
        RefreshCounts();
    }
    public void SetSettingsSelected(bool selected)
    {
        SettingsButton.IsChecked = selected;
        if (!selected)
        {
            UpdateSelection(_page?.ViewModel.IsHistoryView == true);
            return;
        }
        DownloadsButton.IsChecked = false;
        TransferFilters.Visibility = Visibility.Collapsed;
        HistoryButton.IsChecked = false;
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

    private void FiltersRailLoaded(object sender, RoutedEventArgs args) => AttachPage();

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

    private void RefreshCounts()
    {
        if (_page is null) return;
        var counts = _page.ViewModel.SidebarCounts;
        for (var index = 0; index < StatusFilters.Items.Count && index < Labels.Length; index++)
        {
            if (StatusFilters.Items[index] is not Grid row || row.Children.Count < 2 || row.Children[1] is not TextBlock count) continue;
            var value = counts.GetValueOrDefault((DownloadFilter)index);
            count.Text = value.ToString();
            AutomationProperties.SetName(row, $"{Labels[index]}, {value} transfers");
        }
        var history = counts.GetValueOrDefault(DownloadFilter.Completed)
            + counts.GetValueOrDefault(DownloadFilter.Failed)
            + counts.GetValueOrDefault(DownloadFilter.Removed);
        HistoryCount.Text = history.ToString();
        AutomationProperties.SetName(HistoryButton, $"History, {history} records");
    }
}
