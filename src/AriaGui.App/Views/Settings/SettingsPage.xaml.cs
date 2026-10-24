using AriaGui.App.Features;
using AriaGui.App.ViewModels;
using AriaGui.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AriaGui.App.Views.Settings;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    public event RoutedEventHandler? BackRequested;
    private readonly PreferencesViewModel _preferences;
    private Storyboard? _sectionStoryboard;

    public SettingsPage(PreferencesViewModel preferences, FeatureFlags features)
    {
        _preferences = preferences;
        ViewModel = new(preferences, features);
        InitializeComponent();
        DataContext = ViewModel;
        SectionTabs.SelectedItem = SectionTabs.Items[0];
        Unloaded += (_, _) => StopSectionTransition();
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
            6 => new NotificationsSettingsSection(), 7 => new AdvancedSettingsSection(), _ => new AboutSettingsSection()
        };
        content.DataContext = ViewModel;
        SectionContent.Content = content;
        if (IsLoaded) AnimateSectionContent();
    }

    private void AnimateSectionContent()
    {
        StopSectionTransition();
        if (!UiMotion.IsEnabled(_preferences.AnimationsEnabled, XamlRoot))
        {
            SectionContent.Opacity = 1;
            return;
        }
        SectionContent.Opacity = 0;
        _sectionStoryboard = UiMotion.Fade(
            SectionContent, 0, 1, UiMotion.Duration("AriaMotionPageEntryMilliseconds"),
            () =>
            {
                SectionContent.Opacity = 1;
                _sectionStoryboard = null;
            });
    }

    private void StopSectionTransition()
    {
        _sectionStoryboard?.Stop();
        _sectionStoryboard = null;
        if (SectionContent is not null) SectionContent.Opacity = 1;
    }

    public void CompleteMotionImmediately() => StopSectionTransition();

    private void PageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < 800;
        CompactBackButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        SettingsContentColumn.Width = narrow
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(2, GridUnitType.Star);
        SettingsSpacerColumn.Width = narrow
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        UpdateCategoryScrolling();
    }

    private void CategoryScrollerSizeChanged(object sender, SizeChangedEventArgs args) => UpdateCategoryScrolling();
    private void CategoryTabsSizeChanged(object sender, SizeChangedEventArgs args) => UpdateCategoryScrolling();

    private void UpdateCategoryScrolling()
    {
        var overflow = SectionScroller.ScrollableWidth > 0;
        SectionScroller.HorizontalScrollBarVisibility = overflow ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto;
        CategoryScrollHint.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CompactBackClick(object sender, RoutedEventArgs args) => BackRequested?.Invoke(this, args);
}
