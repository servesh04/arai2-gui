using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Downloads;

public sealed partial class DownloadsToolbar : UserControl
{
    public static readonly DependencyProperty IsEngineReadyProperty = DependencyProperty.Register(
        nameof(IsEngineReady), typeof(bool), typeof(DownloadsToolbar), new PropertyMetadata(true, OnEngineReadyChanged));
    public bool IsEngineReady { get => (bool)GetValue(IsEngineReadyProperty); set => SetValue(IsEngineReadyProperty, value); }

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
    private static void OnEngineReadyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var toolbar = (DownloadsToolbar)sender;
        var ready = (bool)args.NewValue;
        toolbar.AddButton.IsEnabled = ready;
        toolbar.FiltersButton.IsEnabled = ready;
        toolbar.SearchBox.IsEnabled = ready;
    }
}
