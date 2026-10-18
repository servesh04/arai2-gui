using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Settings;

internal static class SettingsResetConfirmation
{
    public static async Task ResetAsync(FrameworkElement owner, SettingsViewModel viewModel, string section)
    {
        var dialog = new ContentDialog
        {
            Title = $"Reset {section} settings?",
            Content = "This restores the saved values in this section to their defaults.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await viewModel.ResetSectionAsync(section);
    }
}

public sealed partial class GeneralSettingsSection : UserControl
{
    public GeneralSettingsSection() => InitializeComponent();
    private async void ResetClick(object sender, RoutedEventArgs args) { if (DataContext is SettingsViewModel vm) await SettingsResetConfirmation.ResetAsync(this, vm, "General"); }
}

public sealed partial class AppearanceSettingsSection : UserControl
{
    public AppearanceSettingsSection() => InitializeComponent();
    private async void ResetClick(object sender, RoutedEventArgs args) { if (DataContext is SettingsViewModel vm) await SettingsResetConfirmation.ResetAsync(this, vm, "Appearance"); }
}

public sealed partial class DownloadsSettingsSection : UserControl { public DownloadsSettingsSection() => InitializeComponent(); }
public sealed partial class NetworkSettingsSection : UserControl { public NetworkSettingsSection() => InitializeComponent(); }
public sealed partial class BitTorrentSettingsSection : UserControl { public BitTorrentSettingsSection() => InitializeComponent(); }

public sealed partial class IntegrationSettingsSection : UserControl
{
    public IntegrationSettingsSection() => InitializeComponent();
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        var available = DataContext is SettingsViewModel { BrowserHandoffAvailable: true };
        ComingSoon.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        IntegrationContent.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ReconnectClick(object sender, RoutedEventArgs args) { if (DataContext is SettingsViewModel vm) await vm.CheckBrowserAsync(); }
    private async void ResetClick(object sender, RoutedEventArgs args) { if (DataContext is SettingsViewModel vm) await SettingsResetConfirmation.ResetAsync(this, vm, "Integration"); }
}

public sealed partial class AdvancedSettingsSection : UserControl { public AdvancedSettingsSection() => InitializeComponent(); }
public sealed partial class AboutSettingsSection : UserControl { public AboutSettingsSection() => InitializeComponent(); }
