using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Downloads;

public sealed partial class DownloadsToolbar : UserControl
{
    public DownloadsToolbar() => InitializeComponent();
    public event RoutedEventHandler? AddRequested;
    public event RoutedEventHandler? FiltersRequested;
    public Grid LayoutRoot => ToolbarRoot;
    public StackPanel Primary => PrimaryActions;
    public Button Add => AddButton;
    public Button Filters => FiltersButton;
    public TextBlock FilterText => FilterCaption;
    public TextBox Search => SearchBox;
    public void SetCompact(bool compact) => AddLabel.Text = compact ? "Add" : "Add download";
    private void AddClick(object sender, RoutedEventArgs args) => AddRequested?.Invoke(this, args);
    private void FiltersClick(object sender, RoutedEventArgs args) => FiltersRequested?.Invoke(this, args);
}
