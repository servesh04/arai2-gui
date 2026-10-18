using AriaGui.App.Features;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AriaGui.App.Views.Downloads;

internal static class DownloadFlyoutCoordinator
{
    private static readonly HashSet<FlyoutBase> OpenFlyouts = [];

    public static void Showing(FlyoutBase flyout)
    {
        HideAll(flyout);
        OpenFlyouts.Add(flyout);
    }

    public static void Closed(FlyoutBase flyout) => OpenFlyouts.Remove(flyout);

    public static void HideAll(FlyoutBase? except = null)
    {
        foreach (var flyout in OpenFlyouts.Where(item => !ReferenceEquals(item, except)).ToArray())
            flyout.Hide();
        OpenFlyouts.RemoveWhere(item => !ReferenceEquals(item, except));
    }
}

internal sealed class EngineStatusFlyout
{
    private readonly IEngineControl _engine;
    private readonly FeatureFlags _flags;
    private readonly Flyout _flyout = new();

    public EngineStatusFlyout(IEngineControl engine, FeatureFlags flags)
    {
        _engine = engine;
        _flags = flags;
        _flyout.Opening += (_, _) =>
        {
            _flyout.Content = BuildContent();
            DownloadFlyoutCoordinator.Showing(_flyout);
        };
        _flyout.Closed += (_, _) => DownloadFlyoutCoordinator.Closed(_flyout);
    }

    public void ShowAt(FrameworkElement target) => _flyout.ShowAt(target);

    private UIElement BuildContent()
    {
        var spacing = (double)Microsoft.UI.Xaml.Application.Current.Resources["AriaRadiusControl"];
        var panel = new StackPanel { Spacing = spacing };
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        status.Children.Add(new Ellipse
        {
            Width = spacing,
            Height = spacing,
            Fill = StateBrush(_engine.State),
            VerticalAlignment = VerticalAlignment.Center
        });
        status.Children.Add(new TextBlock { Text = StateText(_engine.State), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Text = StateMessage(_engine.State), TextWrapping = TextWrapping.Wrap });
        if (_engine.State == EngineControlState.Stopped && _flags.EngineRestart)
        {
            var restart = new Button
            {
                Content = "Restart engine",
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AriaPrimaryButtonStyle"]
            };
            restart.Click += RestartClick;
            panel.Children.Add(restart);
        }
        return panel;
    }

    private async void RestartClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        await _engine.RestartAsync();
        _flyout.Content = BuildContent();
    }

    public static string StateText(EngineControlState state) => state switch
    {
        EngineControlState.Ready => "Engine ready",
        EngineControlState.Reconnecting => "Reconnecting…",
        _ => "Engine stopped"
    };

    public static string StateMessage(EngineControlState state) => state switch
    {
        EngineControlState.Ready => "aria2 is connected and ready for transfers.",
        EngineControlState.Reconnecting => "Trying to reconnect to aria2.",
        _ => "Downloads cannot start until aria2 is running."
    };

    public static Brush StateBrush(EngineControlState state)
    {
        var key = state switch
        {
            EngineControlState.Ready => "AriaSuccessBrush",
            EngineControlState.Reconnecting => "AriaWarningBrush",
            _ => "AriaDangerBrush"
        };
        return (Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];
    }
}
