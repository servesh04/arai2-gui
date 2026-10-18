using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Downloads;

public sealed partial class FiltersRail : UserControl
{
    public FiltersRail() => InitializeComponent();
    public ListView Filters => StatusFilters;
    public event RoutedEventHandler? SettingsRequested;
    public event RoutedEventHandler? ViewChanged;
    public void UpdateSelection(bool history)
    {
        TransferFilters.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryButton.IsChecked = history;
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
}
