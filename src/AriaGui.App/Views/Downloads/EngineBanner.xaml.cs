using System.ComponentModel;
using AriaGui.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AriaGui.App.Views.Downloads;

public sealed partial class EngineBanner : UserControl
{
    private DownloadsPage? _page;
    private bool _restarting;

    public EngineBanner()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _page = FindAncestor<DownloadsPage>(this);
        if (_page is null) return;
        _page.Engine.PropertyChanged += EngineChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_page is not null) _page.Engine.PropertyChanged -= EngineChanged;
    }

    private void EngineChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(EngineViewModel.IsReady) or nameof(EngineViewModel.IsBusy) or nameof(EngineViewModel.StatusLabel)) Refresh();
    }

    private void Refresh()
    {
        if (_page is null) return;
        var ready = _page.Engine.IsReady;
        var reconnecting = !ready && _page.Engine.IsBusy;
        Banner.IsOpen = !ready;
        Banner.Severity = reconnecting ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
        Banner.Title = reconnecting ? string.Empty : "Download engine stopped";
        Banner.Message = reconnecting ? "Reconnecting to the download engine…" : "Transfer states may be out of date.";
        RestartButton.Visibility = !reconnecting && _page.Features.EngineRestart ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.IsEnabled = !_restarting;

        if (FindDescendant<DownloadsToolbar>(_page) is { } toolbar) toolbar.IsEngineReady = ready;
        if (FindDescendant<DownloadsTable>(_page) is { } table) table.IsEngineReady = ready;
        if (FindDescendant<SelectionBar>(_page) is { } selection) selection.IsEnabled = ready;
    }

    private async void RestartClick(object sender, RoutedEventArgs args)
    {
        if (_page is null || _restarting) return;
        _restarting = true;
        RestartButton.IsEnabled = false;
        RestartProgress.Visibility = Visibility.Visible;
        RestartProgress.IsActive = true;
        try
        {
            if (_page.Engine.StartCommand.CanExecute(null)) await _page.Engine.StartCommand.ExecuteAsync(null);
        }
        finally
        {
            _restarting = false;
            RestartProgress.IsActive = false;
            RestartProgress.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current)) if (current is T match) return match;
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
