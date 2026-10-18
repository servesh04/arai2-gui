using AriaGui.App.Features;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Downloads;

public sealed partial class StatusBar : UserControl
{
    public StatusBar() => InitializeComponent();
    public DropDownButton CompactStatus => FooterStatus;
    public TextBlock Connection => ConnectionText;
    public TextBlock Total => TotalText;
    public event RoutedEventHandler? EngineRequested;
    public void ApplyFeatures(FeatureFlags flags)
    {
        SpeedLimitButton.IsEnabled = flags.SpeedPresets;
        if (!flags.SpeedPresets) ToolTipService.SetToolTip(SpeedLimitButton, "Speed presets are not available in this build.");
    }
    private void EngineClick(object sender, RoutedEventArgs args) => EngineRequested?.Invoke(this, args);
}
