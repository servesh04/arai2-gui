using System.Collections.ObjectModel;
using AriaGui.App.Features;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AriaGui.App.Views.Downloads;

public sealed class HandoffToastItem(Guid itemId, string message, DateTimeOffset expiresAt, bool aggregate = false)
{
    public Guid ItemId { get; } = itemId;
    public string Message { get; } = message;
    public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    public bool Aggregate { get; } = aggregate;
}

public sealed partial class HandoffToast : UserControl
{
    private readonly ObservableCollection<HandoffToastItem> _items = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IHandoffNotifier _notifier = new UnsupportedHandoffNotifier();
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private bool _paused;
    private int _collapsedCount;

    public event EventHandler<Guid>? ShowRequested;

    public HandoffToast()
    {
        InitializeComponent();
        ToastItems.ItemsSource = _items;
        _timer.Tick += TimerTick;
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public void Initialize(IHandoffNotifier notifier, FeatureFlags flags)
    {
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _notifier.AddedFromBrowser -= AddedFromBrowser;
        _notifier = notifier;
        if (flags.BrowserHandoff) _notifier.AddedFromBrowser += AddedFromBrowser;
    }

    private void AddedFromBrowser(object? sender, HandoffAddedEventArgs args)
        => _uiQueue?.TryEnqueue(() => Add(args));

    private void Add(HandoffAddedEventArgs args)
    {
        var message = $"Added from browser: {args.Name}{(args.AddedPaused ? " (paused)" : string.Empty)}";
        if (_items.Count < 3) _items.Add(new(args.ItemId, message, DateTimeOffset.UtcNow.AddSeconds(8)));
        else
        {
            _collapsedCount++;
            var aggregate = _items.FirstOrDefault(item => item.Aggregate);
            if (aggregate is not null) _items.Remove(aggregate);
            _items.Add(new(Guid.Empty, $"{_collapsedCount + 1} downloads added", DateTimeOffset.UtcNow.AddSeconds(8), true));
        }
    }

    private void TimerTick(object? sender, object args)
    {
        if (_paused) return;
        foreach (var item in _items.Where(item => item.ExpiresAt <= DateTimeOffset.UtcNow).ToArray()) _items.Remove(item);
        if (_items.Count == 0) _collapsedCount = 0;
    }

    private void ShowClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not HandoffToastItem item || item.Aggregate) return;
        ShowRequested?.Invoke(this, item.ItemId);
        _items.Remove(item);
    }

    private async void UndoClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not HandoffToastItem item || item.Aggregate) return;
        if (await _notifier.UndoAsync(item.ItemId)) _items.Remove(item);
    }

    private void CloseClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is HandoffToastItem item) _items.Remove(item);
    }

    private void ToastPointerEntered(object sender, PointerRoutedEventArgs args) => _paused = true;
    private void ToastPointerExited(object sender, PointerRoutedEventArgs args) => _paused = false;
    private void ToastGotFocus(object sender, RoutedEventArgs args) => _paused = true;
    private void ToastLostFocus(object sender, RoutedEventArgs args) => _paused = false;

#if DEBUG
    public void TriggerDebug(string name = "example.iso", bool paused = false)
        => Add(new(Guid.NewGuid(), name, paused));
#endif
}
