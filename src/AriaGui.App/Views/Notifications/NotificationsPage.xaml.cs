using AriaGui.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Notifications;

public sealed partial class NotificationsPage : Page
{
    public NotificationsViewModel ViewModel { get; }

    public NotificationsPage(NotificationsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private async void PageLoaded(object sender, RoutedEventArgs args) => await ViewModel.RefreshAsync();
    private async void RefreshClick(object sender, RoutedEventArgs args) => await ViewModel.RefreshAsync();
    private async void LoadMoreClick(object sender, RoutedEventArgs args) => await ViewModel.LoadMoreAsync();
    private async void MarkAllReadClick(object sender, RoutedEventArgs args) => await ViewModel.MarkAllReadAsync();
    private async void DismissAllClick(object sender, RoutedEventArgs args) => await ViewModel.DismissAllAsync();
    private async void ViewClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: NotificationRowViewModel row }) await ViewModel.ViewDownloadAsync(row);
    }
    private async void MarkReadClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: NotificationRowViewModel row }) await ViewModel.MarkReadAsync(row);
    }
    private async void DismissClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: NotificationRowViewModel row }) await ViewModel.DismissAsync(row);
    }
}
