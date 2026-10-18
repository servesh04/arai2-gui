using System.ComponentModel;
using AriaGui.App.Features;
using AriaGui.App.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AriaGui.App.Views.Downloads;

public sealed partial class StatusBar : UserControl
{
    private readonly TextBlock _compatibilityConnection = new();
    private FeatureFlags _flags = FeatureFlags.Unsupported;
    private IEngineControl _engineControl = new UnsupportedEngineControl();
    private ISpeedLimitService _speedLimitService = new UnsupportedSpeedLimitService();
    private EngineStatusFlyout? _engineFlyout;
    private SpeedLimitFlyout? _speedFlyout;
    private DownloadsPage? _page;
    private MenuFlyout? _queueFlyout;

    public StatusBar()
    {
        InitializeComponent();
        FooterStatus.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
            QueueButton.Visibility = FooterStatus.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        Loaded += StatusBarLoaded;
        Unloaded += (_, _) => DownloadFlyoutCoordinator.HideAll();
    }

    public DropDownButton CompactStatus => FooterStatus;
    public TextBlock Connection => _compatibilityConnection;
    public TextBlock Total => TotalText;
    public event RoutedEventHandler? EngineRequested { add { } remove { } }

    public void ApplyFeatures(FeatureFlags flags)
    {
        _flags = flags;
        RefreshFlyouts();
        RefreshPresentation();
    }

    public void ApplyServices(IEngineControl engineControl, ISpeedLimitService speedLimitService, FeatureFlags flags)
    {
        _engineControl = engineControl;
        _speedLimitService = speedLimitService;
        _flags = flags;
        RefreshFlyouts();
        RefreshPresentation();
    }

    private void StatusBarLoaded(object sender, RoutedEventArgs args)
    {
        _page ??= FindAncestor<DownloadsPage>(this);
        if (_page is not null)
        {
            _engineControl = new PageEngineControl(_page.Engine);
            _page.Engine.PropertyChanged -= SourceChanged;
            _page.Engine.PropertyChanged += SourceChanged;
            _page.Summary.PropertyChanged -= SourceChanged;
            _page.Summary.PropertyChanged += SourceChanged;
        }
        RefreshFlyouts();
        RefreshPresentation();
    }

    private void SourceChanged(object? sender, PropertyChangedEventArgs args) => RefreshPresentation();

    private void RefreshFlyouts()
    {
        _engineFlyout = new EngineStatusFlyout(_engineControl, _flags);
        _speedFlyout = new SpeedLimitFlyout(_speedLimitService);
        _speedFlyout.Changed += (_, _) => RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        var state = _engineControl.State;
        EngineText.Text = EngineStatusFlyout.StateText(state);
        CompactEngineText.Text = EngineText.Text;
        EngineDot.Fill = EngineStatusFlyout.StateBrush(state);
        SpeedText.Text = state == EngineControlState.Ready && _page is not null ? _page.Summary.Speed : "—";
        var presetsAvailable = _flags.SpeedPresets && _speedFlyout?.HasPresets == true;
        SpeedLimitButton.Visibility = presetsAvailable ? Visibility.Visible : Visibility.Collapsed;
        SpeedLimitReadOnly.Visibility = presetsAvailable ? Visibility.Collapsed : Visibility.Visible;
        SpeedLimitText.Text = _speedFlyout?.CurrentLabel ?? "Unlimited";
        SpeedLimitReadOnly.Text = SpeedLimitText.Text;
        AutomationProperties.SetName(SpeedLimitReadOnly, $"Speed limit, {SpeedLimitReadOnly.Text}");
    }

    private void EngineClick(object sender, RoutedEventArgs args) => _engineFlyout?.ShowAt(EngineButton);
    private void SpeedLimitClick(object sender, RoutedEventArgs args) => _speedFlyout?.ShowAt(SpeedLimitButton);

    private void QueueClick(object sender, RoutedEventArgs args)
    {
        if (_page is null) return;
        var active = int.TryParse(_page.Summary.Active, out var activeCount) ? activeCount : 0;
        var waiting = int.TryParse(_page.Summary.Waiting, out var waitingCount) ? waitingCount : 0;
        if (active > 0 && waiting == 0) { ApplyFilter(1); return; }
        if (waiting > 0 && active == 0) { ApplyFilter(2); return; }
        _queueFlyout?.Hide();
        _queueFlyout = new MenuFlyout();
        var activeItem = new MenuFlyoutItem { Text = $"{active} active", IsEnabled = active > 0, Tag = 1 };
        var waitingItem = new MenuFlyoutItem { Text = $"{waiting} waiting", IsEnabled = waiting > 0, Tag = 2 };
        activeItem.Click += QueueFilterClick;
        waitingItem.Click += QueueFilterClick;
        _queueFlyout.Items.Add(activeItem);
        _queueFlyout.Items.Add(waitingItem);
        _queueFlyout.Opening += (_, _) => DownloadFlyoutCoordinator.Showing(_queueFlyout);
        _queueFlyout.Closed += (_, _) => DownloadFlyoutCoordinator.Closed(_queueFlyout);
        _queueFlyout.ShowAt(QueueButton);
    }

    private void QueueFilterClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is int filter) ApplyFilter(filter);
    }

    private void ApplyFilter(int filter)
    {
        if (_page is null) return;
        _page.ViewModel.IsHistoryView = false;
        _page.ViewModel.FilterIndex = filter;
    }

    private void CompactStatusOpening(object sender, object args)
    {
        if (sender is FlyoutBase flyout) DownloadFlyoutCoordinator.Showing(flyout);
    }

    private void CompactStatusClosed(object sender, object args)
    {
        if (sender is FlyoutBase flyout) DownloadFlyoutCoordinator.Closed(flyout);
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private sealed class PageEngineControl(EngineViewModel engine) : IEngineControl
    {
        public EngineControlState State => engine.IsReady
            ? EngineControlState.Ready
            : engine.IsBusy ? EngineControlState.Reconnecting : EngineControlState.Stopped;

        public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            if (!engine.StartCommand.CanExecute(null)) return false;
            engine.StartCommand.Execute(null);
            if (engine.StartCommand.ExecutionTask is { } task) await task.WaitAsync(cancellationToken);
            return engine.IsReady;
        }
    }
}
