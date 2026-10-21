using AriaGui.App.Features;
using AriaGui.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Settings;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    public event RoutedEventHandler? BackRequested;

    public SettingsPage(PreferencesViewModel preferences, FeatureFlags features)
    {
        ViewModel = new(preferences, features);
        InitializeComponent();
        DataContext = ViewModel;
        SectionTabs.SelectedItem = SectionTabs.Items[0];
    }

    public void SelectSection(string name)
    {
        foreach (var item in SectionTabs.Items)
            if (item.Text == name) { SectionTabs.SelectedItem = item; return; }
    }

    private void SectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index < 0) return;
        ShowSection(index);
    }

    private void ShowSection(int index)
    {
        SectionTitle.Text = SectionTabs.Items[index].Text;
        FrameworkElement content = index switch
        {
            0 => new GeneralSettingsSection(), 1 => new AppearanceSettingsSection(), 2 => new DownloadsSettingsSection(),
            3 => new NetworkSettingsSection(), 4 => new BitTorrentSettingsSection(), 5 => new IntegrationSettingsSection(),
            6 => new AdvancedSettingsSection(), _ => new AboutSettingsSection()
        };
        content.DataContext = ViewModel;
        SectionContent.Content = content;
    }

    private void PageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < 800;
        CompactBackButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CompactBackClick(object sender, RoutedEventArgs args) => BackRequested?.Invoke(this, args);
}
