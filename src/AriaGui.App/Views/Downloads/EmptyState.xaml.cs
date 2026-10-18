using System.ComponentModel;
using AriaGui.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AriaGui.App.Views.Downloads;

public sealed partial class EmptyState : UserControl
{
    private DownloadsPage? _page;
    private bool _requestedVisible;

    public EmptyState()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void SetVisible(bool visible) { _requestedVisible = visible; Refresh(); }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _page = FindAncestor<DownloadsPage>(this);
        if (_page is null) return;
        _page.ViewModel.PropertyChanged += ViewModelChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_page is not null) _page.ViewModel.PropertyChanged -= ViewModelChanged;
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DownloadsViewModel.SearchText) or nameof(DownloadsViewModel.FilterIndex) or nameof(DownloadsViewModel.IsEmpty)) Refresh();
    }

    private void Refresh()
    {
        EmptyRoot.Visibility = _requestedVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!_requestedVisible || _page is null) return;
        var query = _page.ViewModel.SearchText.Trim();
        var filter = _page.ViewModel.FilterIndex;
        var searched = query.Length > 0;
        StateIcon.Glyph = searched ? "\uE721" : filter == 0 ? "\uE896" : "\uE8D7";
        TitleText.Text = searched ? $"No results for ‘{query}’" : filter == 0 ? "No downloads yet" : $"No {FilterName(filter)} downloads";
        MessageText.Text = filter == 0 && !searched ? "Paste a link, or drop a link, .torrent or .metalink file here" : string.Empty;
        AddButton.Visibility = filter == 0 && !searched ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = searched ? Visibility.Visible : Visibility.Collapsed;
        BrowserSetup.Visibility = filter == 0 && !searched && _page.Features.BrowserHandoff ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FilterName(int filter) => filter switch
    {
        1 => "downloading", 2 => "waiting", 3 => "paused", 4 => "interrupted",
        5 => "completed", 6 => "failed", 7 => "stopped", _ => "matching"
    };

    private void AddClick(object sender, RoutedEventArgs args) => InvokeToolbarAdd();
    private void ClearClick(object sender, RoutedEventArgs args) { if (_page is not null) _page.ViewModel.SearchText = string.Empty; }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(StandardDataFormats.Text) || args.DataView.Contains(StandardDataFormats.StorageItems)) args.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs args)
    {
        if (_page is null) return;
        string? value = null;
        if (args.DataView.Contains(StandardDataFormats.Text)) value = (await args.DataView.GetTextAsync()).Trim();
        else if (args.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var item = (await args.DataView.GetStorageItemsAsync()).FirstOrDefault();
            if (item is not null && (item.Path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || item.Path.EndsWith(".metalink", StringComparison.OrdinalIgnoreCase))) value = item.Path;
        }
        if (string.IsNullOrWhiteSpace(value)) return;
        _page.ViewModel.InputUrl = value;
        InvokeToolbarAdd();
    }

    private void InvokeToolbarAdd()
    {
        if (_page is null || FindDescendant<DownloadsToolbar>(_page) is not { } toolbar) return;
        new ButtonAutomationPeer(toolbar.Add).Invoke();
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
