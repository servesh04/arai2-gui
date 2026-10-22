using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Settings;

public sealed partial class NotificationsSettingsSection : UserControl
{
    public NotificationsSettingsSection() => InitializeComponent();

    private async void ResetClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is SettingsViewModel viewModel)
            await SettingsResetConfirmation.ResetAsync(this, viewModel, "Notifications");
    }
}
