using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AriaGui.App.Views.Downloads;

public sealed partial class DownloadsToolbar : UserControl
{
    private bool _compact;
    private bool _searchOpen;
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
    public void SetCompact(bool compact)
    {
        _compact = compact;
        AddLabel.Text = compact ? "Add" : "Add download";
        SearchButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = !compact || _searchOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    public void RevealSearch()
    {
        _searchOpen = true;
        SearchBox.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Keyboard);
    }
    private void SearchClick(object sender, RoutedEventArgs args)
    {
        if (_searchOpen)
        {
            _searchOpen = false;
            SearchBox.Visibility = Visibility.Collapsed;
            SearchButton.Focus(FocusState.Programmatic);
        }
        else RevealSearch();
    }
    private void AddClick(object sender, RoutedEventArgs args) => AddRequested?.Invoke(this, args);
    private void FiltersClick(object sender, RoutedEventArgs args) => FiltersRequested?.Invoke(this, args);
    private static void OnEngineReadyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var toolbar = (DownloadsToolbar)sender;
        var ready = (bool)args.NewValue;
        toolbar.AddButton.IsEnabled = ready;
        ToolTipService.SetToolTip(toolbar.AddButton, ready ? "Add download" : "Engine stopped. Restart to add downloads.");
    }
}
