using AriaGui.App.Features;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Downloads;

internal sealed class SpeedLimitFlyout
{
    private readonly ISpeedLimitService _service;
    private readonly MenuFlyout _flyout = new();

    public SpeedLimitFlyout(ISpeedLimitService service)
    {
        _service = service;
        _flyout.Opening += (_, _) =>
        {
            BuildItems();
            DownloadFlyoutCoordinator.Showing(_flyout);
        };
        _flyout.Closed += (_, _) => DownloadFlyoutCoordinator.Closed(_flyout);
    }

    public event EventHandler? Changed;
    public string CurrentLabel => _service.Current?.Name ?? "Unlimited";
    public bool HasPresets => _service.Presets.Count > 0;
    public void ShowAt(FrameworkElement target) => _flyout.ShowAt(target);

    private void BuildItems()
    {
        _flyout.Items.Clear();
        foreach (var preset in _service.Presets)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = preset.Name,
                GroupName = "SpeedLimit",
                IsChecked = Equals(_service.Current, preset),
                Tag = preset
            };
            item.Click += PresetClick;
            _flyout.Items.Add(item);
        }
    }

    private async void PresetClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not SpeedLimitPreset preset) return;
        await _service.SetAsync(preset);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
