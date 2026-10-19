using AriaGui.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Windows.System;
using AriaGui.Application.Downloads;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using System.Windows.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;
using AriaGui.App.Features;

namespace AriaGui.App.Views;

public sealed partial class DownloadsPage : Page
{
    public DownloadsViewModel ViewModel { get; }
    public DownloadSummaryViewModel Summary { get; }
    public EngineViewModel Engine { get; }
    public PreferencesViewModel Preferences { get; }
    public FeatureFlags Features { get; }
    private Grid Toolbar => DownloadsToolbarControl.LayoutRoot;
    private StackPanel PrimaryActions => DownloadsToolbarControl.Primary;
    private StackPanel SelectedActions => SelectionBarControl.Actions;
    private Button AddButton => DownloadsToolbarControl.Add;
    private Button FiltersButton => DownloadsToolbarControl.Filters;
    private TextBlock FilterCaption => DownloadsToolbarControl.FilterText;
    private TextBox SearchBox => DownloadsToolbarControl.Search;
    private Button RetryButton => SelectionBarControl.Retry;
    private MenuFlyout MoreMenu => SelectionBarControl.Menu;
    private MenuFlyoutItem OverflowRetry => SelectionBarControl.OverflowRetryItem;
    private ListView StatusFilters => FiltersRailControl.Filters;
    private ListView TransferList => DownloadsTableControl.List;
    private ScrollViewer TableScroller => DownloadsTableControl.Scroller;
    private Grid TableCanvas => DownloadsTableControl.Canvas;
    private Grid TableHeader => DownloadsTableControl.Header;
    private Grid Inspector => DetailsPaneControl.Root;
    private ScrollViewer DetailsHost => DetailsPaneControl.Host;
    private TextBlock SelectionHint => DetailsPaneControl.Hint;
    private StackPanel InspectorMetadata => DetailsPaneControl.Metadata;
    private Button InspectorOpenFile => DetailsPaneControl.OpenFile;
    private Button InspectorOpenFolder => DetailsPaneControl.OpenFolder;
    private DropDownButton FooterStatus => StatusBarControl.CompactStatus;
    private TextBlock ConnectionText => StatusBarControl.Connection;
    private TextBlock TotalText => StatusBarControl.Total;
    public event Action? ExitRequested;
    private bool _dialogOpen;
    private bool _shellUpdatePending;
    private bool _paneRootSubscribed;
    private ShellLayoutMode _shellMode;
    private readonly Flyout _filtersFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private Control? _overlayFilterFocus;
    private Control? _dockedFilterFocus;
    private static readonly string[] FilterLabels = ["All transfers", "Downloading", "Waiting", "Paused", "Needs recovery", "Completed", "Failed", "Stopped"];
    private ContentDialog? _activeDialog;
    private double _detailsHeight = 180;
    private double _detailsWidth = 360;
    private bool _detailsSideDocked;
    private bool _restoringSelection;
    private bool _hadSelection;
    private bool _detailsOpenForSelection;
    private bool _replacingSelection;
    private readonly HashSet<Guid> _selection = [];
    private readonly HashSet<CheckBox> _rowChecks = [];
    private bool _exiting;
    private bool _settingsVisible;
    private DownloadItemViewModel? _observedSelected;
    private readonly DispatcherTimer _layoutSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public DownloadsPage(DownloadsViewModel viewModel, DownloadSummaryViewModel summary, EngineViewModel engine, PreferencesViewModel preferences,
        FeatureFlags features, IHandoffNotifier handoffNotifier, IEngineControl engineControl, ISpeedLimitService speedLimitService)
    {
        ViewModel = viewModel;
        Summary = summary;
        Engine = engine;
        Preferences = preferences;
        Features = features;
#if DEBUG
        if (AriaGui.App.Services.AppDataPaths.VerificationDirectory is not null)
        {
            var noticePath = System.IO.Path.Combine(AppContext.BaseDirectory, "r2-fixture-notices.json");
            if (System.IO.File.Exists(noticePath))
            {
                var notices = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(noticePath))!;
                ViewModel.SeedVerificationNotices(notices.GetValueOrDefault("Batch", ""), notices.GetValueOrDefault("Error", ""), notices.GetValueOrDefault("Completion", ""));
            }
        }
#endif
        InitializeComponent();
        DataContext = this;
        DownloadsTableControl.Initialize(ViewModel, Preferences, ScheduleLayoutSave, RefreshRowChecks);
        DownloadsTableControl.SelectionChanged += TransferSelectionChanged;
        DownloadsTableControl.RowRightTapped += TransferRightTapped;
        DownloadsTableControl.RowCheckReady += DownloadTableRowCheckReady;
        DownloadsTableControl.RealizedContainersChanged += (_, _) => RefreshRowChecks();
        StatusBarControl.ApplyServices(engineControl, speedLimitService, features);
        HandoffToastControl.Initialize(handoffNotifier, features);
        HandoffToastControl.ShowRequested += ShowHandoffItem;
        FiltersRailControl.ViewChanged += (_, _) => ShowDownloads();
#if DEBUG
        var debugHandoff = new MenuFlyoutItem { Text = "Show browser handoff toast" };
        debugHandoff.Click += (_, _) => HandoffToastControl.TriggerDebug();
        HelpMenu.Items.Add(debugHandoff);
#endif
        // Thumb handles pointer presses internally; receive handled presses too so
        // subsequent keyboard arrows act on the grip the user just selected.
        SidebarSplitter.AddHandler(PointerPressedEvent, new PointerEventHandler(GripPointerPressed), true);
        DetailsSplitter.AddHandler(PointerPressedEvent, new PointerEventHandler(GripPointerPressed), true);
        SidebarHost.GettingFocus += (_, args) => _overlayFilterFocus = args.NewFocusedElement as Control;
        _filtersFlyout.Closed += (_, _) =>
        {
            _overlayFilterFocus = null;
            if (_dockedFilterFocus is not { } target) return;
            _dockedFilterFocus = null;
            // Hide restores the flyout opener's focus. Restore the descendant afterward.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!target.Focus(FocusState.Keyboard)) StatusFilters.Focus(FocusState.Keyboard);
            });
        };
        // Opt-in read-only layout evidence; the destination is an explicit test artifact.
        var geometryPath = Environment.GetEnvironmentVariable("ARAI_TABLE_GEOMETRY_PATH");
#if DEBUG
        geometryPath ??= System.IO.Path.Combine(AppContext.BaseDirectory, "table-geometry.jsonl");
#endif
        if (!string.IsNullOrWhiteSpace(geometryPath))
        {
            var lastGeometry = "";
            LayoutUpdated += (_, _) =>
            {
                if (XamlRoot is null || TableCanvas.ActualWidth <= 0) return;
                object Bounds(FrameworkElement element)
                {
                    var origin = element.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point());
                    return new { origin.X, origin.Y, Width = element.ActualWidth, Height = element.ActualHeight };
                }
                var snapshot = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Scale = XamlRoot.RasterizationScale, Client = Bounds(this), Workspace = Bounds(TransferWorkspace),
                    PanePreferences = new { Preferences.SidebarWidth, Preferences.DetailsHeight, Preferences.DetailsVisible, Preferences.Layout.TableSizingMode },
                    EffectivePanes = new { SidebarWidth = SidebarColumn.Width.Value, DetailsHeight = DetailsRow.Height.Value, DetailsVisible = Inspector.Visibility == Visibility.Visible },
                    Details = Bounds(Inspector), Notices = Bounds(NoticeScroller), History = Bounds(HistoryBar),
                    ViewportWidth = FindAncestorScrollViewer(TableCanvas)?.ViewportWidth,
                    Canvas = Bounds(TableCanvas), Header = Bounds(TableHeader), HeaderPadding = TableHeader.Padding.ToString(),
                    PreferenceColumns = Preferences.Columns,
                    HeaderWidths = TableHeader.ColumnDefinitions.Select(c => c.ActualWidth).ToArray(),
                    SelectedJobIds = _selection.ToArray(), SelectedCount = TransferList.SelectedItems.Count,
                    Rows = DownloadsTableControl.RegisteredRows.Keys.Select(g => new { GridId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g), BoundJobId = (g.DataContext as DownloadItemViewModel)?.Id, Bounds = Bounds(g), Widths = g.ColumnDefinitions.Select(c => c.ActualWidth).ToArray() }).ToArray()
                });
                if (snapshot == lastGeometry) return;
                lastGeometry = snapshot;
                try { System.IO.File.AppendAllText(geometryPath, snapshot + Environment.NewLine); }
                catch (System.IO.IOException) { }
                catch (UnauthorizedAccessException) { }
            };
        }
        Loaded += (_, _) =>
        {
            ApplyLayout(); UpdateShellLayout(); UpdateBatchNotice();
            BatchDetailsButton.Visibility = ViewModel.BatchResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_paneRootSubscribed && XamlRoot is { } root)
            {
                root.Changed += (_, _) => { UpdateShellLayout(); UpdatePaneLayout(); SizeActiveDialog(); };
                _paneRootSubscribed = true;
            }
        };
        SizeChanged += ShellSizeChanged;
        StatusFilters.SelectionChanged += (_, _) => UpdateFilterCaption();
        LayoutUpdated += (_, _) => UpdatePaneLayout();
        Preferences.PropertyChanged += PreferencesChanged;
        ViewModel.Downloads.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(RestoreSelection);
        _layoutSaveTimer.Tick += async (_, _) => { _layoutSaveTimer.Stop(); await Preferences.SaveLayoutAsync(); };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SidebarCounts)) RefreshSidebarCounts();
            if (e.PropertyName == nameof(ViewModel.IsHistoryView)) UpdateFilterCaption();
            if (e.PropertyName == nameof(ViewModel.BatchMessage)) UpdateBatchNotice();
            if (e.PropertyName == nameof(ViewModel.BatchResults)) BatchDetailsButton.Visibility = ViewModel.BatchResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (e.PropertyName == nameof(ViewModel.IsEmpty)) EmptyStateControl.SetVisible(ViewModel.IsEmpty);
        };
    }
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        return null;
    }
    private void FindInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { _filtersFlyout.Hide(); SearchBox.Focus(FocusState.Keyboard); args.Handled = true; }
    private async void AddInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; await ShowDialogAsync(new Downloads.AddDownloadDialog(ViewModel)); }
    private async void AddClick(object sender, RoutedEventArgs args) => await ShowDialogAsync(new Downloads.AddDownloadDialog(ViewModel));
    private void PreferencesClick(object sender, RoutedEventArgs args)
    {
        if (SettingsHost.Content is not Settings.SettingsPage)
        {
            var settings = new Settings.SettingsPage(Preferences, Features);
            settings.BackRequested += (_, _) => ShowDownloads();
            SettingsHost.Content = settings;
        }
        _settingsVisible = true;
        DownloadsContent.Visibility = Visibility.Collapsed;
        HandoffToastControl.Visibility = Visibility.Collapsed;
        SettingsHost.Visibility = Visibility.Visible;
        FiltersRailControl.SetSettingsSelected(true);
    }
    private void ShowDownloads()
    {
        _settingsVisible = false;
        SettingsHost.Visibility = Visibility.Collapsed;
        DownloadsContent.Visibility = Visibility.Visible;
        HandoffToastControl.Visibility = Visibility.Visible;
        FiltersRailControl.SetSettingsSelected(false);
    }
    private void ShowHandoffItem(object? sender, Guid itemId)
    {
        ShowDownloads();
        ViewModel.IsHistoryView = false;
        ViewModel.FilterIndex = 0;
        ViewModel.SearchText = string.Empty;
        DispatcherQueue.TryEnqueue(() =>
        {
            var item = ViewModel.Downloads.FirstOrDefault(row => row.Id == itemId);
            if (item is null) return;
            TransferList.SelectedItems.Clear();
            TransferList.SelectedItems.Add(item);
            TransferList.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
            TransferList.Focus(FocusState.Programmatic);
        });
    }
    private void ExitClick(object sender, RoutedEventArgs args) => ExitRequested?.Invoke();

    private async Task ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen || _exiting || XamlRoot is null) return;
        _dialogOpen = true;
        _activeDialog = dialog;
        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = ActualTheme;
        if (dialog.Content is not ScrollViewer)
        {
            var body = dialog.Content;
            dialog.Content = null; // Detach before giving the element a new parent.
            dialog.Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        SizeActiveDialog();
        if (Preferences.AnimationsEnabled && new UISettings().AnimationsEnabled)
            dialog.Opened += (_, _) =>
            {
                if (dialog.Content is not UIElement content) return;
                var animation = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(170)) };
                Storyboard.SetTarget(animation, content); Storyboard.SetTargetProperty(animation, "Opacity");
                var storyboard = new Storyboard(); storyboard.Children.Add(animation); storyboard.Begin();
            };
        try { await dialog.ShowAsync(); }
        catch (OperationCanceledException) { }
        catch (System.Runtime.InteropServices.COMException) when (_exiting || XamlRoot is null) { }
        finally { _activeDialog = null; _dialogOpen = false; }
    }
    public void PrepareForExit() { _exiting = true; _layoutSaveTimer.Stop(); _activeDialog?.Hide(); Preferences.PropertyChanged -= PreferencesChanged; }
    public Task SaveLayoutAsync() => Preferences.SaveLayoutAsync();

    private void SizeActiveDialog()
    {
        if (_activeDialog is not { } dialog || ActualWidth <= 0 || ActualHeight <= 0) return;
        var width = Math.Max(0, Math.Min(560, ActualWidth - 32));
        var height = Math.Max(0, ActualHeight - 32);
        dialog.Resources["ContentDialogMinWidth"] = 0d;
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMaxHeight"] = height;
        // ContentDialog's template centers its content over the full window. Keep
        // that outer surface unconstrained; cap the template and scrolling body.
        dialog.MinWidth = 0;
        if (dialog.Content is ScrollViewer body)
        {
            body.Width = Math.Max(0, width - 48);
            body.MaxHeight = Math.Max(40, height - 140);
            body.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }

    private async void EngineClick(object sender, RoutedEventArgs args)
    {
        var content = new StackPanel { Spacing = 12, DataContext = Engine };
        foreach (var property in new[] { nameof(Engine.StatusLabel), nameof(Engine.Message), nameof(Engine.Version) })
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(property) });
            content.Children.Add(text);
        }
        var actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(new Button { Content = "Start engine", Command = Engine.StartCommand });
        actions.Children.Add(new Button { Content = "Check connection", Command = Engine.CheckCommand });
        actions.Children.Add(new Button { Content = "Stop verified older engine (keep files)", Command = Engine.StopLegacyCommand });
        content.Children.Add(actions);
        await ShowDialogAsync(new ContentDialog { Title = "Download engine", Content = content, CloseButtonText = "Close" });
    }
    private async void HelpClick(object sender, RoutedEventArgs args) => await ShowDialogAsync(new ContentDialog
    {
        Title = "Keyboard shortcuts", CloseButtonText = "Close",
        Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Ctrl+N  Add a download\nCtrl+F  Filter transfers\nArrow keys  Select a transfer\nTab  Move between controls\n\nFocus a pane divider and use its arrow keys to resize it.\nClose keeps downloads running in the tray. File → Exit saves and stops the engine." }
    });
    private async void BatchDetailsClick(object sender, RoutedEventArgs args)
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (var result in ViewModel.BatchResults)
        {
            var name = ViewModel.Downloads.FirstOrDefault(item => item.Id == result.JobId)?.FileName ?? result.JobId.ToString();
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = $"{result.Outcome}: {result.Message}", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            rows.Children.Add(row);
        }
        await ShowDialogAsync(new ContentDialog { Title = "Batch results", CloseButtonText = "Close",
            Content = rows });
    }
    private async void InspectorDetailsClick(object sender, RoutedEventArgs args)
    {
        if (TransferList.SelectedItem is not DownloadItemViewModel selected) return;
        var content = new StackPanel { Spacing = 10, DataContext = selected };
        foreach (var property in new[] { nameof(selected.FileName), nameof(selected.Status), nameof(selected.SizeLabel), nameof(selected.QueueLabel), nameof(selected.Source), nameof(selected.Destination), nameof(selected.ErrorMessage) })
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            text.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(property) });
            content.Children.Add(text);
        }
        content.Children.Add(new Button { Content = "Open file", Command = selected.OpenFileCommand });
        content.Children.Add(new Button { Content = "Open folder", Command = selected.OpenFolderCommand });
        await ShowDialogAsync(new ContentDialog { Title = "Transfer details", Content = content, CloseButtonText = "Close" });
    }

    private void TransferSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_restoringSelection) return;
        var hasSelection = TransferList.SelectedItems.Count > 0;
        if (!hasSelection) _detailsOpenForSelection = false;
        else if (!_hadSelection && !_replacingSelection) _detailsOpenForSelection = true;
        _hadSelection = hasSelection;
        foreach (DownloadItemViewModel item in args.RemovedItems)
            if (ViewModel.Downloads.Contains(item)) _selection.Remove(item.Id);
        foreach (DownloadItemViewModel item in args.AddedItems) _selection.Add(item.Id);
        ViewModel.SetSelection(_selection);
        RefreshSelectionPresentation();
    }
    private void RefreshSelectionPresentation()
    {
        RefreshRowChecks();
        var selected = TransferList.SelectedItem as DownloadItemViewModel;
        TransferMenu.IsEnabled = selected is not null;
        foreach (var item in TransferMenu.Items) item.DataContext = selected;
        foreach (var item in MoreMenu.Items) item.DataContext = selected;
        foreach (var item in TransferMenu.Items.Concat(MoreMenu.Items).OfType<MenuFlyoutItem>())
            if (item.Tag is string tag) item.IsEnabled = TransferList.SelectedItems.Count == 1 && SingleCommand(selected, tag)?.CanExecute(null) == true;
        DetailsHost.DataContext = selected;
        InspectorDetailsMenu.IsEnabled = selected is not null;
        InspectorOpenFile.IsEnabled = TransferList.SelectedItems.Count == 1 && selected?.OpenFileCommand.CanExecute(null) == true;
        InspectorOpenFolder.IsEnabled = TransferList.SelectedItems.Count == 1 && selected?.OpenFolderCommand.CanExecute(null) == true;
        if (_observedSelected != selected)
        {
            if (_observedSelected is not null) foreach (var command in _observedSelected.Commands) command.CanExecuteChanged -= SelectedCommandChanged;
            _observedSelected = selected;
            if (selected is not null) foreach (var command in selected.Commands) command.CanExecuteChanged += SelectedCommandChanged;
        }
        DetailsHost.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        SelectionHint.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        SelectionBarControl.Visibility = _selection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePaneLayout();
    }
    private void SelectedCommandChanged(object? sender, EventArgs args) => RefreshSelectionPresentation();
    private void RowCheckLoaded(object sender, RoutedEventArgs args)
    {
        RegisterRowCheck((CheckBox)sender);
    }
    private void DownloadTableRowCheckReady(object? sender, CheckBox check) => RegisterRowCheck(check);
    private void RegisterRowCheck(CheckBox check)
    {
        if (_rowChecks.Add(check)) { check.DataContextChanged += RowCheckDataContextChanged; check.Unloaded += RowCheckUnloaded; }
        RefreshRowCheck(check);
    }
    private void RowCheckUnloaded(object sender, RoutedEventArgs args)
    {
        var check = (CheckBox)sender;
        _rowChecks.Remove(check); check.DataContextChanged -= RowCheckDataContextChanged; check.Unloaded -= RowCheckUnloaded;
    }
    private void RowCheckDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => RefreshRowCheck((CheckBox)sender);
    private void RefreshRowCheck(CheckBox check) => check.IsChecked = check.DataContext is DownloadItemViewModel item && _selection.Contains(item.Id);
    private static IEnumerable<CheckBox> FindRowChecks(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CheckBox check && check.Tag is "TransferSelectionCheck") yield return check;
            foreach (var descendant in FindRowChecks(child)) yield return descendant;
        }
    }
    private void RefreshRowChecks()
    {
        // A recycled ListView container can remain loaded while its template is reattached.
        // Discover realized controls as well as servicing Loaded/DataContextChanged events.
        foreach (var check in FindRowChecks(TransferList)) RegisterRowCheck(check);
        foreach (var check in _rowChecks.ToArray()) RefreshRowCheck(check);
    }
    private void RowCheckTapped(object sender, TappedRoutedEventArgs args) => args.Handled = true;
    private void RowCheckClick(object sender, RoutedEventArgs args)
    {
        var check = (CheckBox)sender;
        if (check.DataContext is not DownloadItemViewModel item || !ViewModel.Downloads.Contains(item)) return;
        if (check.IsChecked == true)
        {
            if (!TransferList.SelectedItems.Contains(item)) TransferList.SelectedItems.Add(item);
        }
        else TransferList.SelectedItems.Remove(item);
        RefreshRowChecks();
    }
    private void RefreshSidebarCounts()
    {
        for (var i = 0; i < StatusFilters.Items.Count; i++)
        {
            if (StatusFilters.Items[i] is not Grid row) continue;
            var number = ViewModel.SidebarCounts.GetValueOrDefault((DownloadFilter)i);
            ((TextBlock)row.Children[1]).Text = number.ToString();
            AutomationProperties.SetName(row, $"{FilterLabels[i]}, {number} transfers");
        }
        UpdateFilterCaption();
        UpdateBatchNotice();
    }
    private void UpdateFilterCaption()
    {
        if (FiltersButton is null) return;
        var index = Math.Clamp(ViewModel.FilterIndex, 0, FilterLabels.Length - 1);
        FiltersRailControl.UpdateSelection(ViewModel.IsHistoryView);
        if (_settingsVisible) FiltersRailControl.SetSettingsSelected(true);
        FilterCaption.Text = $"Filters: {FilterLabels[index]}";
        ToolTipService.SetToolTip(FiltersButton, $"{FilterLabels[index]}{(ViewModel.IsHistoryView ? ", saved history" : "")}");
        AutomationProperties.SetName(FiltersButton, $"Filters: {FilterLabels[index]}{(ViewModel.IsHistoryView ? ", saved history" : "")}");
    }
    private void UpdateBatchNotice() => BatchMessageText.Visibility = string.IsNullOrWhiteSpace(ViewModel.BatchMessage) ? Visibility.Collapsed : Visibility.Visible;
    private void ShellSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_shellUpdatePending) return;
        _shellUpdatePending = true;
        DispatcherQueue.TryEnqueue(() => { _shellUpdatePending = false; UpdateShellLayout(); });
    }
    private static double NaturalWidth(UIElement element)
    {
        element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }
    private void UpdateShellLayout()
    {
        if (Toolbar is null || ActualWidth <= 0) return;
        // Page ActualWidth is the client area in DIPs; never infer it from physical window bounds.
        FiltersButton.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        var commands = NaturalWidth(PrimaryActions) + NaturalWidth(SelectedActions) + 40;
        _shellMode = ResponsiveShellLayoutPolicy.Resolve(ActualWidth, Math.Max(1100, commands + 240), Math.Max(800, commands));
        var wide = _shellMode == ShellLayoutMode.Wide;
        var compact = _shellMode == ShellLayoutMode.Compact;
        RetryButton.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        OverflowRetry.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(SearchBox, wide ? 0 : 1); Grid.SetColumn(SearchBox, wide ? 1 : 0);
        Grid.SetColumnSpan(SearchBox, wide ? 1 : 2);
        SearchBox.Width = wide ? Math.Clamp(ActualWidth - commands, 240, 420) : double.NaN;
        SearchBox.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        SearchBox.Margin = new Thickness(0, wide ? 0 : 6, 0, 0);
        DownloadsToolbarControl.SetCompact(compact);
        FiltersButton.Visibility = compact || !Preferences.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        var docked = !compact && Preferences.SidebarVisible;
        if (docked) RestoreDockedFilters();
        else
        {
            if (Workspace.Children.Contains(SidebarHost)) SidebarHost.Visibility = Visibility.Collapsed;
            else SidebarHost.MaxHeight = Math.Max(120, ActualHeight - 160);
            SidebarColumn.Width = new GridLength(0);
        }
        Workspace.ColumnDefinitions[1].Width = new GridLength(docked ? 6 : 0);
        SidebarSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(HistoryActions, compact ? 1 : 0); Grid.SetColumn(HistoryActions, compact ? 0 : 1);
        Grid.SetColumnSpan(HistoryActions, compact ? 2 : 1);
        ConnectionText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FooterStatus.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        TotalText.MaxWidth = compact ? 90 : 180;
        SelectionBarControl.Visibility = _selection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilterCaption();
        UpdatePaneLayout();
        SizeActiveDialog();
    }
    private void RestoreDockedFilters()
    {
        if (!Workspace.Children.Contains(SidebarHost))
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            var restoreFocus = false;
            for (var ancestor = focused; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ReferenceEquals(ancestor, SidebarHost)) { restoreFocus = true; break; }
            _dockedFilterFocus = _overlayFilterFocus ?? (restoreFocus ? focused as Control : null);
            _filtersFlyout.Hide();
            _filtersFlyout.Content = null;
            Workspace.Children.Insert(0, SidebarHost);
        }
        if (!double.IsNaN(SidebarHost.Width)) SidebarHost.Width = double.NaN;
        SidebarHost.MaxHeight = double.PositiveInfinity;
        SidebarHost.Visibility = Visibility.Visible;
        UpdatePaneLayout();
    }
    private void FiltersClick(object sender, RoutedEventArgs args)
    {
        _overlayFilterFocus = null;
        Workspace.Children.Remove(SidebarHost);
        SidebarHost.Visibility = Visibility.Visible;
        SidebarHost.Width = Math.Min(280, Math.Max(180, ActualWidth - 48));
        // Keep the overlay's own ScrollViewer inside a short client; history and
        // preferences remain reachable by scrolling instead of extending below it.
        SidebarHost.MaxHeight = Math.Max(120, ActualHeight - 160);
        _filtersFlyout.Content = SidebarHost;
        _filtersFlyout.ShowAt(FiltersButton);
    }

    private void SetSidebarWidth(double width)
    {
        Preferences.SidebarWidth = Math.Clamp(width, 120, 400);
        UpdatePaneLayout();
    }
    private void SetDetailsHeight(double height)
    {
        Preferences.DetailsHeight = Math.Clamp(height, 120, 600);
        UpdatePaneLayout();
    }
    private void SetDetailsWidth(double width)
    {
        Preferences.DetailsWidth = Math.Clamp(width, 280, 600);
        UpdatePaneLayout();
    }
    private void UpdatePaneLayout()
    {
        if (Workspace is null || Workspace.ActualHeight <= 0) return;
        Downloads.DownloadsTable.SetGripAutomation(SidebarSplitter, "sidebar width", Preferences.SidebarWidth, 120, 400, "left and right");
        _detailsSideDocked = ActualWidth >= 1100;
        var layout = ResponsivePaneLayoutPolicy.Resolve(Workspace.ActualHeight + NoticeScroller.ActualHeight,
            HistoryBar.ActualHeight, Workspace.ActualWidth, Preferences.DetailsHeight, Preferences.SidebarWidth,
            Preferences.DetailsVisible || _detailsOpenForSelection, TransferList.SelectedItem is not null,
            !string.IsNullOrWhiteSpace(ViewModel.BatchMessage) || ViewModel.HasError || ViewModel.HasCompletion,
            Preferences.SidebarVisible && _shellMode != ShellLayoutMode.Compact,
            _detailsSideDocked ? ResponsivePaneLayoutPolicy.MinimumTableHeight : ResponsivePaneLayoutPolicy.MinimumBottomDockTableHeight);
        if (Math.Abs(NoticeScroller.MaxHeight - layout.NoticeHeight) > .1) NoticeScroller.MaxHeight = layout.NoticeHeight;
        if (Math.Abs(SidebarColumn.Width.Value - layout.SidebarWidth) > .1) SidebarColumn.Width = new GridLength(layout.SidebarWidth);
        _detailsHeight = layout.DetailsHeight;
        _detailsWidth = Preferences.DetailsWidth;
        var effectiveWidth = Math.Min(_detailsWidth, Math.Max(0, TransferWorkspace.ActualWidth - 426));
        var visible = TransferList.SelectedItem is not null && (Preferences.DetailsVisible || _detailsOpenForSelection)
            && (_detailsSideDocked ? effectiveWidth >= 280 : layout.DetailsHeight > 0);
        DetailsToggle.IsChecked = Preferences.DetailsVisible || _detailsOpenForSelection;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (Inspector.Visibility != visibility) Inspector.Visibility = visibility;
        if (DetailsSplitter.Visibility != visibility) DetailsSplitter.Visibility = visibility;
        Grid.SetColumn(TransferListHost, 0); Grid.SetColumnSpan(TransferListHost, _detailsSideDocked ? 1 : 3);
        Grid.SetRow(DetailsSplitter, _detailsSideDocked ? 1 : 2); Grid.SetColumn(DetailsSplitter, _detailsSideDocked ? 1 : 0);
        Grid.SetColumnSpan(DetailsSplitter, _detailsSideDocked ? 1 : 3);
        Grid.SetRow(DetailsPaneControl, _detailsSideDocked ? 1 : 3); Grid.SetColumn(DetailsPaneControl, _detailsSideDocked ? 2 : 0);
        Grid.SetColumnSpan(DetailsPaneControl, _detailsSideDocked ? 1 : 3);
        DetailsGripColumn.Width = new GridLength(_detailsSideDocked && visible ? 6 : 0);
        DetailsColumn.Width = new GridLength(_detailsSideDocked && visible ? effectiveWidth : 0);
        DetailsGripRow.Height = new GridLength(!_detailsSideDocked && visible ? 6 : 0);
        DetailsRow.Height = new GridLength(!_detailsSideDocked && visible ? layout.DetailsHeight : 0);
        Downloads.DownloadsTable.SetGripAutomation(DetailsSplitter, _detailsSideDocked ? "details pane width" : "details pane height",
            _detailsSideDocked ? Preferences.DetailsWidth : Preferences.DetailsHeight, _detailsSideDocked ? 280 : 120, 600,
            _detailsSideDocked ? "left and right" : "up and down");
        var detailsText = TransferList.SelectedItem is not null && (Preferences.DetailsVisible || _detailsOpenForSelection) && !visible
            ? "Details pane (collapsed to fit)" : "Details pane";
        if (DetailsToggle.Text != detailsText) DetailsToggle.Text = detailsText;
        var orientation = !_detailsSideDocked && TransferWorkspace.ActualWidth >= 800 ? Orientation.Horizontal : Orientation.Vertical;
        if (InspectorMetadata.Orientation != orientation) InspectorMetadata.Orientation = orientation;
    }
    private void SidebarDragDelta(object sender, DragDeltaEventArgs args) { SetSidebarWidth(SidebarColumn.ActualWidth + args.HorizontalChange); SavePaneLayout(); }
    private void DetailsDragDelta(object sender, DragDeltaEventArgs args)
    {
        if (_detailsSideDocked) SetDetailsWidth(_detailsWidth - args.HorizontalChange);
        else SetDetailsHeight(_detailsHeight - args.VerticalChange);
        SavePaneLayout();
    }
    private void GripPointerPressed(object sender, PointerRoutedEventArgs args) => ((Thumb)sender).Focus(FocusState.Pointer);
    private void SidebarKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        SetSidebarWidth(SidebarColumn.ActualWidth + (args.Key == VirtualKey.Right ? 10 : -10)); SavePaneLayout(); args.Handled = true;
    }
    private void DetailsKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_detailsSideDocked)
        {
            if (args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
            SetDetailsWidth(_detailsWidth + (args.Key == VirtualKey.Left ? 10 : -10));
        }
        else
        {
            if (args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
            SetDetailsHeight(_detailsHeight + (args.Key == VirtualKey.Up ? 10 : -10));
        }
        SavePaneLayout(); args.Handled = true;
    }
    private void WorkspaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (DetailsToggle is null || TransferWorkspace is null) return;
        UpdatePaneLayout();
        ShellSizeChanged(sender, args);
    }
    private void DetailsClick(object sender, RoutedEventArgs args)
    {
        Preferences.DetailsVisible = DetailsToggle.IsChecked;
        _detailsOpenForSelection = DetailsToggle.IsChecked && TransferList.SelectedItem is not null;
        UpdatePaneLayout();
        ScheduleLayoutSave();
    }
    internal void CloseDetailsFromPane()
    {
        _detailsOpenForSelection = false;
        Preferences.DetailsVisible = false;
        UpdatePaneLayout();
    }
    private void ResetPanesClick(object sender, RoutedEventArgs args) { SetSidebarWidth(160); SetDetailsHeight(180); SetDetailsWidth(360); SavePaneLayout(); }
    private void RestoreSelection()
    {
        _selection.IntersectWith(ViewModel.Downloads.Select(item => item.Id));
        _restoringSelection = true;
        try
        {
            foreach (var item in ViewModel.Downloads)
                if (_selection.Contains(item.Id) && !TransferList.SelectedItems.Contains(item)) TransferList.SelectedItems.Add(item);
        }
        finally { _restoringSelection = false; }
        _hadSelection = TransferList.SelectedItems.Count > 0;
        if (!_hadSelection) _detailsOpenForSelection = false;
        ViewModel.SetSelection(_selection);
        RefreshSelectionPresentation();
    }
    private void SelectAllClick(object sender, RoutedEventArgs args) => TransferList.SelectAll();
    private void ClearSelectionClick(object sender, RoutedEventArgs args) { _selection.Clear(); TransferList.SelectedItems.Clear(); ViewModel.SetSelection(_selection); }
    private void SelectAllInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        TransferList.SelectAll(); args.Handled = true;
    }
    private void SingleActionClick(object sender, RoutedEventArgs args)
    {
        if (TransferList.SelectedItems.Count != 1 || TransferList.SelectedItem is not DownloadItemViewModel item) return;
        var tag = (sender as FrameworkElement)?.Tag as string;
        var command = SingleCommand(item, tag);
        if (command?.CanExecute(null) == true) command.Execute(null);
    }
    private static ICommand? SingleCommand(DownloadItemViewModel? item, string? tag) => item is null ? null : tag switch
    { "MoveUp" => item.MoveUpCommand, "MoveDown" => item.MoveDownCommand, "OpenFile" => item.OpenFileCommand, "OpenFolder" => item.OpenFolderCommand, _ => null };
    private void TransferRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null && source is not ListViewItem) source = VisualTreeHelper.GetParent(source);
        if (source is ListViewItem container && TransferList.ItemFromContainer(container) is DownloadItemViewModel item
            && !TransferList.SelectedItems.Contains(item))
        {
            _replacingSelection = TransferList.SelectedItems.Count > 0;
            try { TransferList.SelectedItems.Clear(); TransferList.SelectedItems.Add(item); }
            finally { _replacingSelection = false; }
        }
        var menu = new MenuFlyout();
        foreach (var (label, command) in new (string, ICommand)[] { ("Resume", ViewModel.BatchResumeCommand), ("Pause", ViewModel.BatchPauseCommand),
            ("Retry", ViewModel.BatchRetryCommand), ("Recover", ViewModel.BatchRecoverCommand), ("Restart fresh (keep partial files)", ViewModel.BatchRestartFreshCommand), ("Stop transfer (keep files and history)", ViewModel.BatchRemoveCommand) })
            menu.Items.Add(new MenuFlyoutItem { Text = label, Command = command });
        foreach (var (label, tag) in new[] { ("Move earlier in queue", "MoveUp"), ("Move later in queue", "MoveDown"), ("Open file", "OpenFile"), ("Open folder", "OpenFolder") })
        {
            var entry = new MenuFlyoutItem { Text = label, Tag = tag, IsEnabled = TransferList.SelectedItems.Count == 1 && SingleCommand(TransferList.SelectedItem as DownloadItemViewModel, tag)?.CanExecute(null) == true };
            entry.Click += SingleActionClick; menu.Items.Add(entry);
        }
        menu.ShowAt(TransferList, new FlyoutShowOptions { Position = args.GetPosition(TransferList) }); args.Handled = true;
    }
    private void PreferencesChanged(object? sender, PropertyChangedEventArgs args) => ApplyLayout();
    private void ScheduleLayoutSave() { _layoutSaveTimer.Stop(); _layoutSaveTimer.Start(); }
    private void SavePaneLayout()
    {
        ScheduleLayoutSave();
    }
    private void ApplyLayout()
    {
        if (TableHeader is null) return;
        RefreshSidebarCounts();
        EmptyStateControl.SetVisible(ViewModel.IsEmpty);
        DetailsToggle.IsChecked = Preferences.DetailsVisible || _detailsOpenForSelection;
        _detailsHeight = Preferences.DetailsHeight;
        _detailsWidth = Preferences.DetailsWidth;
        SidebarColumn.Width = new GridLength(Preferences.SidebarVisible ? Preferences.SidebarWidth : 0);
        SidebarHost.Visibility = Preferences.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        Workspace.ColumnDefinitions[1].Width = new GridLength(Preferences.SidebarVisible ? 6 : 0);
        SidebarSplitter.Visibility = Preferences.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateShellLayout();
        DownloadsTableControl.ApplyLayout();
    }
    private async void ColumnsClick(object sender, RoutedEventArgs args) =>
        await DownloadsTableControl.ShowColumnsAsync(ShowDialogAsync);
}
