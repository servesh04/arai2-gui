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
        SectionList.SelectedIndex = 0;
        SectionPicker.SelectedIndex = 0;
        SectionList.SelectionChanged += SectionChanged;
        SectionPicker.SelectionChanged += SectionChanged;
        Loaded += (_, _) => ShowSection(0);
    }

    private void SectionChanged(object sender, SelectionChangedEventArgs args)
    {
        var index = ReferenceEquals(sender, SectionList) ? SectionList.SelectedIndex : SectionPicker.SelectedIndex;
        if (index < 0) return;
        if (SectionList.SelectedIndex != index) SectionList.SelectedIndex = index;
        if (SectionPicker.SelectedIndex != index) SectionPicker.SelectedIndex = index;
        ShowSection(index);
    }

    private void ShowSection(int index)
    {
        SectionTitle.Text = SectionList.Items[index]?.ToString() ?? string.Empty;
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
        SectionColumn.Width = narrow ? new GridLength(0) : new GridLength(180);
        SectionList.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        SectionPicker.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(SectionPicker, narrow ? 1 : 0);
    }

    private void CompactBackClick(object sender, RoutedEventArgs args) => BackRequested?.Invoke(this, args);
}
