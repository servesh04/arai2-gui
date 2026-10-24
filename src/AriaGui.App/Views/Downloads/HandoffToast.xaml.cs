using System.Collections.ObjectModel;
using AriaGui.App.Features;
using AriaGui.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

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
    private readonly Dictionary<HandoffToastItem, Border> _cards = [];
    private readonly Dictionary<HandoffToastItem, Storyboard> _itemTransitions = [];
    private readonly HashSet<HandoffToastItem> _entrancePlayed = [];
    private readonly HashSet<HandoffToastItem> _exiting = [];
    private IHandoffNotifier _notifier = new UnsupportedHandoffNotifier();
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private Func<bool>? _animationsEnabled;
    private bool _paused;
    private int _collapsedCount;

    public event EventHandler<Guid>? ShowRequested;

    public HandoffToast()
    {
        InitializeComponent();
        ToastItems.ItemsSource = _items;
        _timer.Tick += TimerTick;
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            foreach (var item in _itemTransitions.Keys.ToArray())
            {
                if (_exiting.Contains(item)) FinalizeToastRemoval(item);
                else if (_cards.TryGetValue(item, out var card)) CancelItemTransition(item, card);
            }
        };
    }

    public void Initialize(IHandoffNotifier notifier, FeatureFlags flags, Func<bool> animationsEnabled)
    {
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _animationsEnabled = animationsEnabled;
        _notifier.AddedFromBrowser -= AddedFromBrowser;
        _notifier = notifier;
        if (flags.BrowserHandoff) _notifier.AddedFromBrowser += AddedFromBrowser;
    }

    public void CompleteTransitionsImmediately()
    {
        foreach (var item in _itemTransitions.Keys.ToArray())
        {
            if (_exiting.Contains(item)) FinalizeToastRemoval(item);
            else if (_cards.TryGetValue(item, out var card)) CancelItemTransition(item, card);
        }
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
            if (aggregate is not null) RemoveToastItem(aggregate, animate: false);
            _items.Add(new(Guid.Empty, $"{_collapsedCount + 1} downloads added", DateTimeOffset.UtcNow.AddSeconds(8), true));
        }
    }

    private void TimerTick(object? sender, object args)
    {
        if (_paused) return;
        foreach (var item in _items.Where(item => item.ExpiresAt <= DateTimeOffset.UtcNow && !_exiting.Contains(item)).ToArray())
            RemoveToastItem(item, animate: true);
        if (_items.Count == 0) _collapsedCount = 0;
    }

    private void ShowClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not HandoffToastItem item || item.Aggregate) return;
        ShowRequested?.Invoke(this, item.ItemId);
        RemoveToastItem(item, animate: true);
    }

    private async void UndoClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not HandoffToastItem item || item.Aggregate) return;
        if (await _notifier.UndoAsync(item.ItemId)) RemoveToastItem(item, animate: true);
    }

    private void CloseClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is HandoffToastItem item) RemoveToastItem(item, animate: true);
    }

    private void ToastCardLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Border card || card.DataContext is not HandoffToastItem item) return;
        _cards[item] = card;
        var transform = EnsureTransform(card);
        if (!_entrancePlayed.Add(item))
        {
            card.Opacity = 1;
            transform.Y = 0;
            return;
        }
        if (!UiMotion.IsEnabled(_animationsEnabled?.Invoke() == true, XamlRoot))
        {
            card.Opacity = 1;
            transform.Y = 0;
            return;
        }

        var duration = UiMotion.Duration("AriaMotionToastEnterMilliseconds");
        if (duration <= 0)
        {
            card.Opacity = 1;
            transform.Y = 0;
            return;
        }
        CancelItemTransition(item, card);
        card.IsHitTestVisible = true;
        card.Opacity = 0;
        transform.Y = UiMotion.Duration("AriaMotionToastOffsetY");
        var opacity = new DoubleAnimation
        {
            From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(duration)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var offset = new DoubleAnimation
        {
            From = transform.Y, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(duration)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, card);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(offset, transform);
        Storyboard.SetTargetProperty(offset, "Y");
        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(offset);
        storyboard.Completed += (_, _) =>
        {
            card.Opacity = 1;
            transform.Y = 0;
            if (_itemTransitions.TryGetValue(item, out var active) && ReferenceEquals(active, storyboard))
                _itemTransitions.Remove(item);
        };
        _itemTransitions[item] = storyboard;
        storyboard.Begin();
    }

    private void ToastCardUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Border card || card.DataContext is not HandoffToastItem item) return;
        if (_exiting.Contains(item))
        {
            FinalizeToastRemoval(item);
            return;
        }
        CancelItemTransition(item, card);
        card.Opacity = 1;
        EnsureTransform(card).Y = 0;
        _cards.Remove(item);
    }

    private void RemoveToastItem(HandoffToastItem item, bool animate)
    {
        if (!_items.Contains(item) || !_exiting.Add(item)) return;
        if (!animate || !_cards.TryGetValue(item, out var card)
            || !UiMotion.IsEnabled(_animationsEnabled?.Invoke() == true, XamlRoot)
            || !MoveFocusOffCard(card))
        {
            FinalizeToastRemoval(item);
            return;
        }

        CancelItemTransition(item, card);
        var transform = EnsureTransform(card);
        card.Opacity = 1;
        transform.Y = 0;
        card.IsHitTestVisible = false;
        foreach (var control in VisualDescendants(card).OfType<Control>())
        {
            control.IsTabStop = false;
            control.IsEnabled = false;
        }
        var duration = UiMotion.Duration("AriaMotionToastExitMilliseconds");
        if (duration <= 0)
        {
            FinalizeToastRemoval(item);
            return;
        }
        var opacity = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(duration)) };
        Storyboard.SetTarget(opacity, card);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Completed += (_, _) => FinalizeToastRemoval(item);
        _itemTransitions[item] = storyboard;
        storyboard.Begin();
    }

    private bool MoveFocusOffCard(Border card)
    {
        if (XamlRoot is null || !IsDescendantOf(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, card)) return true;
        if (FocusManager.TryMoveFocus(FocusNavigationDirection.Next)
            && !IsDescendantOf(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, card)) return true;
        if (FocusManager.TryMoveFocus(FocusNavigationDirection.Previous)
            && !IsDescendantOf(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, card)) return true;
        return false;
    }

    private void FinalizeToastRemoval(HandoffToastItem item)
    {
        if (_itemTransitions.Remove(item, out var transition)) transition.Stop();
        if (_cards.Remove(item, out var card))
        {
            card.Opacity = 1;
            card.IsHitTestVisible = true;
            EnsureTransform(card).Y = 0;
        }
        _exiting.Remove(item);
        _entrancePlayed.Remove(item);
        _items.Remove(item);
        if (_items.Count == 0) _collapsedCount = 0;
    }

    private void CancelItemTransition(HandoffToastItem item, Border card)
    {
        if (_itemTransitions.Remove(item, out var transition)) transition.Stop();
        card.Opacity = 1;
        EnsureTransform(card).Y = 0;
    }

    private static TranslateTransform EnsureTransform(Border card)
    {
        if (card.RenderTransform is TranslateTransform transform) return transform;
        transform = new TranslateTransform();
        card.RenderTransform = transform;
        return transform;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
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
