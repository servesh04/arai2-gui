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

namespace AriaGui.App.Views;

public sealed partial class DownloadsPage : Page
{
    public DownloadsViewModel ViewModel { get; }
    public DownloadSummaryViewModel Summary { get; }
    public EngineViewModel Engine { get; }
    public PreferencesViewModel Preferences { get; }
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
    private bool _restoringSelection;
    private readonly HashSet<Guid> _selection = [];
    private readonly HashSet<CheckBox> _rowChecks = [];
    private readonly Dictionary<Grid, int[]> _cellColumns = [];
    private string _headerLayout = "";
    private DownloadTableSizingMode _tableSizingMode = DownloadTableSizingMode.Fit;
    private IReadOnlyList<DownloadColumnLayout> _resolvedColumns = [];
    private double _tableLeadingInset = 32; // Selection column, refined from the realized template.
    private double _tableTrailingInset;
    private bool _exiting;
    private DownloadItemViewModel? _observedSelected;
    private readonly DispatcherTimer _layoutSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public DownloadsPage(DownloadsViewModel viewModel, DownloadSummaryViewModel summary, EngineViewModel engine, PreferencesViewModel preferences)
    {
        ViewModel = viewModel;
        Summary = summary;
        Engine = engine;
        Preferences = preferences;
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
                    Rows = _cellColumns.Keys.Select(g => new { GridId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g), BoundJobId = (g.DataContext as DownloadItemViewModel)?.Id, Bounds = Bounds(g), Widths = g.ColumnDefinitions.Select(c => c.ActualWidth).ToArray() }).ToArray()
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
        LayoutUpdated += (_, _) => { MeasureTableInsets(); UpdatePaneLayout(); };
        Preferences.PropertyChanged += PreferencesChanged;
        ViewModel.Downloads.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(RestoreSelection);
        _layoutSaveTimer.Tick += async (_, _) => { _layoutSaveTimer.Stop(); await Preferences.SaveLayoutAsync(); };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SidebarCounts)) RefreshSidebarCounts();
            if (e.PropertyName == nameof(ViewModel.IsHistoryView)) UpdateFilterCaption();
            if (e.PropertyName == nameof(ViewModel.BatchMessage)) UpdateBatchNotice();
            if (e.PropertyName == nameof(ViewModel.BatchResults)) BatchDetailsButton.Visibility = ViewModel.BatchResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
    { args.Handled = true; await ShowDialogAsync(new AddDownloadDialog(ViewModel)); }
    private async void AddClick(object sender, RoutedEventArgs args) => await ShowDialogAsync(new AddDownloadDialog(ViewModel));
    private async void PreferencesClick(object sender, RoutedEventArgs args) => await ShowDialogAsync(new PreferencesDialog(Preferences, ViewModel));
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
        SelectedActions.DataContext = selected;
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
        UpdatePaneLayout();
    }
    private void SelectedCommandChanged(object? sender, EventArgs args) => RefreshSelectionPresentation();
    private void RowCheckLoaded(object sender, RoutedEventArgs args)
    {
        RegisterRowCheck((CheckBox)sender);
    }
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
        AddButton.Content = "＋ Add download";
        FiltersButton.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        var commands = NaturalWidth(PrimaryActions) + NaturalWidth(SelectedActions) + 40;
        _shellMode = ResponsiveShellLayoutPolicy.Resolve(ActualWidth, Math.Max(1100, commands + 240), Math.Max(800, commands));
        var wide = _shellMode == ShellLayoutMode.Wide;
        var compact = _shellMode == ShellLayoutMode.Compact;
        RetryButton.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        OverflowRetry.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(SearchBox, wide ? 0 : 1); Grid.SetColumn(SearchBox, wide ? 2 : 0);
        Grid.SetColumnSpan(SearchBox, wide ? 1 : 3);
        SearchBox.Width = wide ? Math.Clamp(ActualWidth - commands, 240, 420) : double.NaN;
        SearchBox.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        SearchBox.Margin = new Thickness(0, wide ? 0 : 6, 0, 0);
        AddButton.Content = compact ? "＋ Add" : "＋ Add download";
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
        var splitCommands = NaturalWidth(PrimaryActions) + NaturalWidth(SelectedActions) + 30 > ActualWidth;
        Grid.SetRow(SelectedActions, splitCommands ? 1 : 0);
        Grid.SetColumn(SelectedActions, splitCommands ? 0 : 1);
        Grid.SetColumnSpan(SelectedActions, splitCommands ? 3 : 1);
        if (splitCommands) Grid.SetRow(SearchBox, 2);
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
    private void UpdatePaneLayout()
    {
        if (Workspace is null || Workspace.ActualHeight <= 0) return;
        SetGripAutomation(SidebarSplitter, "sidebar width", Preferences.SidebarWidth, 120, 400, "left and right");
        SetGripAutomation(DetailsSplitter, "details pane height", Preferences.DetailsHeight, 120, 600, "up and down");
        var layout = ResponsivePaneLayoutPolicy.Resolve(Workspace.ActualHeight + NoticeScroller.ActualHeight,
            HistoryBar.ActualHeight, Workspace.ActualWidth, Preferences.DetailsHeight, Preferences.SidebarWidth,
            Preferences.DetailsVisible, TransferList.SelectedItem is not null,
            !string.IsNullOrWhiteSpace(ViewModel.BatchMessage) || ViewModel.HasError || ViewModel.HasCompletion,
            Preferences.SidebarVisible && _shellMode != ShellLayoutMode.Compact);
        if (Math.Abs(NoticeScroller.MaxHeight - layout.NoticeHeight) > .1) NoticeScroller.MaxHeight = layout.NoticeHeight;
        if (Math.Abs(SidebarColumn.Width.Value - layout.SidebarWidth) > .1) SidebarColumn.Width = new GridLength(layout.SidebarWidth);
        _detailsHeight = layout.DetailsHeight;
        var visible = layout.DetailsHeight > 0;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (Inspector.Visibility != visibility) Inspector.Visibility = visibility;
        if (DetailsSplitter.Visibility != visibility) DetailsSplitter.Visibility = visibility;
        var gripHeight = visible ? 6 : 0;
        if (Math.Abs(DetailsGripRow.Height.Value - gripHeight) > .1) DetailsGripRow.Height = new GridLength(gripHeight);
        if (Math.Abs(DetailsRow.Height.Value - layout.DetailsHeight) > .1) DetailsRow.Height = new GridLength(layout.DetailsHeight);
        var detailsText = Preferences.DetailsVisible && !visible ? "Details pane (collapsed to fit)" : "Details pane";
        if (DetailsToggle.Text != detailsText) DetailsToggle.Text = detailsText;
        var orientation = TransferWorkspace.ActualWidth >= 800 ? Orientation.Horizontal : Orientation.Vertical;
        if (InspectorMetadata.Orientation != orientation) InspectorMetadata.Orientation = orientation;
    }
    private void SidebarDragDelta(object sender, DragDeltaEventArgs args) { SetSidebarWidth(SidebarColumn.ActualWidth + args.HorizontalChange); SavePaneLayout(); }
    private void DetailsDragDelta(object sender, DragDeltaEventArgs args) { SetDetailsHeight(_detailsHeight - args.VerticalChange); SavePaneLayout(); }
    private void GripPointerPressed(object sender, PointerRoutedEventArgs args) => ((Thumb)sender).Focus(FocusState.Pointer);
    private void SidebarKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        SetSidebarWidth(SidebarColumn.ActualWidth + (args.Key == VirtualKey.Right ? 10 : -10)); SavePaneLayout(); args.Handled = true;
    }
    private void DetailsKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        SetDetailsHeight(_detailsHeight + (args.Key == VirtualKey.Up ? 10 : -10)); SavePaneLayout(); args.Handled = true;
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
        UpdatePaneLayout();
        ScheduleLayoutSave();
    }
    private void ResetPanesClick(object sender, RoutedEventArgs args) { SetSidebarWidth(160); SetDetailsHeight(180); SavePaneLayout(); }
    private void DownloadContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        AutomationProperties.SetName(args.ItemContainer, args.InRecycleQueue ? "" : (args.Item as DownloadItemViewModel)?.FileName ?? "Download");
        args.ItemContainer.MinHeight = Preferences.CompactRows ? 31 : 39;
        if (!args.InRecycleQueue) DispatcherQueue.TryEnqueue(() => { RefreshRealizedRows(); RefreshRowChecks(); });
    }

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
            && !TransferList.SelectedItems.Contains(item)) { TransferList.SelectedItems.Clear(); TransferList.SelectedItems.Add(item); }
        var menu = new MenuFlyout();
        foreach (var (label, command) in new (string, ICommand)[] { ("Resume", ViewModel.BatchResumeCommand), ("Pause", ViewModel.BatchPauseCommand),
            ("Retry", ViewModel.BatchRetryCommand), ("Recover", ViewModel.BatchRecoverCommand), ("Stop transfer (keep files and history)", ViewModel.BatchRemoveCommand) })
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
        DetailsToggle.IsChecked = Preferences.DetailsVisible;
        _detailsHeight = Preferences.DetailsHeight;
        _tableSizingMode = Preferences.Layout.TableSizingMode;
        SidebarColumn.Width = new GridLength(Preferences.SidebarVisible ? Preferences.SidebarWidth : 0);
        SidebarHost.Visibility = Preferences.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        Workspace.ColumnDefinitions[1].Width = new GridLength(Preferences.SidebarVisible ? 6 : 0);
        SidebarSplitter.Visibility = Preferences.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        Inspector.Visibility = DetailsSplitter.Visibility = Preferences.DetailsVisible ? Visibility.Visible : Visibility.Collapsed;
        DetailsGripRow.Height = new GridLength(Preferences.DetailsVisible ? 6 : 0);
        DetailsRow.Height = new GridLength(Preferences.DetailsVisible ? _detailsHeight : 0);
        UpdateShellLayout();
        var signature = string.Join(";", Preferences.Columns.Select(c => $"{c.Column}:{c.Visible}"));
        if (_headerLayout != signature) { _headerLayout = signature; BuildHeader(); }
        // Width-only edits keep existing grips so keyboard/drag focus survives.
        foreach (var grip in TableHeader.Children.OfType<Thumb>())
            if (grip.Tag is DownloadSortColumn column)
                SetGripAutomation(grip, $"{ColumnLabel(column)} column width", Preferences.Columns.First(c => c.Column == column).Width,
                    DesktopLayout.MinimumColumnWidth, DesktopLayout.MaximumColumnWidth, "left and right");
        ResolveTableWidths();
        foreach (var grid in _cellColumns.Keys.ToArray()) ConfigureRow(grid);
        foreach (var item in ViewModel.Downloads)
            if (TransferList.ContainerFromItem(item) is ListViewItem container) container.MinHeight = Preferences.CompactRows ? 31 : 39;
    }
    private void TableViewportChanged(object sender, SizeChangedEventArgs args) => ResolveTableWidths();
    private void TableViewportViewChanged(object sender, ScrollViewerViewChangedEventArgs args) => ResolveTableWidths();
    private void MeasureTableInsets()
    {
        if (TableCanvas.ActualWidth <= 0 || TransferList.ActualHeight <= 0) return;
        // Kept-alive selected/recycled containers can be parked far outside the viewport.
        // Their global transforms are not measurements of native item chrome.
        var row = _cellColumns.Keys.FirstOrDefault(g =>
        {
            if (!g.IsLoaded || g.ActualHeight <= 0 || g.DataContext is not DownloadItemViewModel item
                || TransferList.ContainerFromItem(item) is not ListViewItem) return false;
            var position = g.TransformToVisual(TransferList).TransformPoint(new Windows.Foundation.Point());
            return position.Y + g.ActualHeight > 0 && position.Y < TransferList.ActualHeight
                && position.X >= 0 && position.X <= 128;
        });
        if (row is null) return;
        var leading = row.TransformToVisual(TableCanvas).TransformPoint(new Windows.Foundation.Point()).X;
        var nativeScroller = FindAncestorScrollViewer(row);
        if (nativeScroller is null || ReferenceEquals(nativeScroller, TableScroller) || nativeScroller.ViewportWidth <= 0) return;
        // The native scrolling viewport owns reserved scrollbar space. Unused row extent
        // and TableCanvas.Width must never feed back into the chrome budget.
        var trailing = Math.Max(0, TransferList.ActualWidth - nativeScroller.ViewportWidth);
        if (!double.IsFinite(leading) || !double.IsFinite(trailing)
            || leading < 0 || leading > 128 || trailing > 64) return;
        if (Math.Abs(leading - _tableLeadingInset) < .25 && Math.Abs(trailing - _tableTrailingInset) < .25) return;
        _tableLeadingInset = leading;
        _tableTrailingInset = trailing;
        ResolveTableWidths();
    }
    private void ResolveTableWidths()
    {
        if (TableScroller is null || TableHeader is null) return;
        var viewport = TableScroller.ViewportWidth > 0 ? TableScroller.ViewportWidth : TableScroller.ActualWidth;
        if (viewport <= 0) return;
        var resolved = DownloadTableWidthResolver.Resolve(Preferences.Columns, viewport,
            _tableLeadingInset + _tableTrailingInset, _tableSizingMode);
        var unchanged = _resolvedColumns.SequenceEqual(resolved.Columns)
            && Math.Abs(TableCanvas.Width - resolved.CanvasWidth) < .1
            && Math.Abs(TableHeader.Padding.Left - _tableLeadingInset) < .1
            && Math.Abs(TableHeader.Padding.Right - _tableTrailingInset) < .1;
        _resolvedColumns = resolved.Columns;
        TableCanvas.Width = resolved.CanvasWidth;
        TableHeader.Padding = new Thickness(_tableLeadingInset, 0, _tableTrailingInset, 0);
        for (var i = 0; i < TableHeader.ColumnDefinitions.Count; i++)
            TableHeader.ColumnDefinitions[i].Width = new GridLength(ResolvedWidth(Preferences.Columns[i].Column));
        RefreshRealizedRows();
        if (!unchanged) foreach (var grid in _cellColumns.Keys.ToArray()) ConfigureRow(grid);
    }
    private double ResolvedWidth(DownloadSortColumn column) => _resolvedColumns.FirstOrDefault(c => c.Column == column)?.Width ?? 0;
    private void RowLoaded(object sender, RoutedEventArgs args)
    {
        RegisterRowGrid((Grid)sender);
    }
    private void RegisterRowGrid(Grid grid)
    {
        if (!_cellColumns.ContainsKey(grid))
        {
            _cellColumns[grid] = grid.Children.Cast<FrameworkElement>().Select(cell => Convert.ToInt32(cell.Tag)).ToArray();
            grid.Unloaded += RowUnloaded;
        }
        ConfigureRow(grid);
    }
    private void RowUnloaded(object sender, RoutedEventArgs args)
    {
        var grid = (Grid)sender;
        _cellColumns.Remove(grid);
        grid.Unloaded -= RowUnloaded;
    }
    private void RefreshRealizedRows()
    {
        // History/filter collection changes can reattach a cached template without Loaded.
        // Discover the native realized tree so every displayed row receives shared widths.
        void Visit(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Grid grid && grid.Tag is "TransferDataGrid")
                {
                    if (!_cellColumns.ContainsKey(grid)) RegisterRowGrid(grid);
                }
                else Visit(child);
            }
        }
        Visit(TransferList);
    }
    private void ConfigureRow(Grid grid)
    {
        if (!_cellColumns.TryGetValue(grid, out var original)) return;
        grid.ColumnDefinitions.Clear();
        foreach (var column in Preferences.Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ResolvedWidth(column.Column)) });
        grid.Width = _resolvedColumns.Sum(c => c.Width);
        for (var i = 0; i < grid.Children.Count; i++)
        {
            var column = (DownloadSortColumn)(original[i] + 1);
            var index = Preferences.Columns.ToList().FindIndex(c => c.Column == column);
            if (index < 0) continue;
            Grid.SetColumn((FrameworkElement)grid.Children[i], index);
            ((FrameworkElement)grid.Children[i]).Visibility = Preferences.Columns[index].Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        grid.MinHeight = Preferences.CompactRows ? 31 : 39;
    }
    private static string ColumnLabel(DownloadSortColumn column) => column == DownloadSortColumn.Speed ? "Down speed" : column.ToString();
    private void BuildHeader()
    {
        TableHeader.Children.Clear(); TableHeader.ColumnDefinitions.Clear();
        for (var i = 0; i < Preferences.Columns.Count; i++)
        {
            var column = Preferences.Columns[i];
            TableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ResolvedWidth(column.Column)) });
            if (!column.Visible) continue;
            var button = new Button { Content = ColumnLabel(column.Column) + (ViewModel.SortColumn == column.Column ? (ViewModel.SortDescending ? " ↓" : " ↑") : ""),
                Padding = new Thickness(8, 0, 12, 0), MinHeight = 28, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0) };
            button.Click += (_, _) => { ViewModel.SetSort(column.Column); BuildHeader(); };
            Grid.SetColumn(button, i); TableHeader.Children.Add(button);
            var grip = new Thumb { Width = 7, HorizontalAlignment = HorizontalAlignment.Right,
                IsTabStop = true, UseSystemFocusVisuals = true, Tag = column.Column };
            grip.AddHandler(PointerPressedEvent, new PointerEventHandler(GripPointerPressed), true);
            SetGripAutomation(grip, $"{ColumnLabel(column.Column)} column width", column.Width,
                DesktopLayout.MinimumColumnWidth, DesktopLayout.MaximumColumnWidth, "left and right");
            grip.DragDelta += (_, e) => ResizeColumn(column.Column, e.HorizontalChange);
            grip.KeyDown += (_, e) => { if (e.Key is VirtualKey.Left or VirtualKey.Right) { ResizeColumn(column.Column, e.Key == VirtualKey.Right ? 10 : -10); e.Handled = true; } };
            Grid.SetColumn(grip, i); TableHeader.Children.Add(grip);
        }
    }
    private void ResizeColumn(DownloadSortColumn column, double delta)
    {
        var columns = Preferences.Columns.Select(c => c.Visible ? c with { Width = Math.Clamp(ResolvedWidth(c.Column), DesktopLayout.MinimumColumnWidth, DesktopLayout.MaximumColumnWidth) } : c).ToArray();
        _tableSizingMode = DownloadTableSizingMode.Manual;
        Preferences.UpdateLayout(Preferences.Layout with { TableSizingMode = DownloadTableSizingMode.Manual, Columns = columns.Select(c => c.Column == column ? c with { Width = Math.Clamp(ResolvedWidth(column) + delta, DesktopLayout.MinimumColumnWidth, DesktopLayout.MaximumColumnWidth) } : c).ToArray() });
        ScheduleLayoutSave();
    }
    private static void SetGripAutomation(Thumb grip, string label, double value, double minimum, double maximum, string keys)
    {
        // Thumb is sealed in WinUI. Preserve its native drag/focus behavior and expose
        // the current preferred value through readable UIA properties, not a fake RangeValue pattern.
        var name = $"Resize {label}, {value:0.#} device-independent pixels; use {keys} arrows";
        var status = $"Preferred size {value:0.#}; range {minimum:0.#} to {maximum:0.#} device-independent pixels";
        if (AutomationProperties.GetName(grip) != name) AutomationProperties.SetName(grip, name);
        if (AutomationProperties.GetItemStatus(grip) != status) AutomationProperties.SetItemStatus(grip, status);
    }
    private async void ColumnsClick(object sender, RoutedEventArgs args)
    {
        var panel = new StackPanel { Spacing = 8 };
        void Refresh()
        {
            panel.Children.Clear();
            var sizing = new ComboBox { Header = "Column sizing", ItemsSource = new[] { "Fit viewport", "Manual widths" }, SelectedIndex = _tableSizingMode == DownloadTableSizingMode.Fit ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
            sizing.SelectionChanged += (_, _) => { Preferences.UpdateLayout(Preferences.Layout with { TableSizingMode = sizing.SelectedIndex == 0 ? DownloadTableSizingMode.Fit : DownloadTableSizingMode.Manual }); ScheduleLayoutSave(); };
            panel.Children.Add(sizing);
            foreach (var column in Preferences.Columns)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var check = new CheckBox { Content = ColumnLabel(column.Column), IsChecked = column.Visible, Width = 160, IsEnabled = column.Column != DownloadSortColumn.Name };
                check.Click += (_, _) => { if (check.IsChecked != true && Preferences.Columns.Count(c => c.Visible) <= 1) { check.IsChecked = true; return; }
                    Preferences.UpdateLayout(Preferences.Layout with { Columns = Preferences.Columns.Select(c => c.Column == column.Column ? c with { Visible = check.IsChecked == true } : c).ToArray() }); ScheduleLayoutSave(); };
                row.Children.Add(check);
                foreach (var (label, offset) in new[] { ("←", -1), ("→", 1) })
                {
                    var button = new Button { Content = label, Padding = new Thickness(8, 3, 8, 3) };
                    AutomationProperties.SetName(button, $"Move {ColumnLabel(column.Column)} {(offset < 0 ? "earlier" : "later")}");
                    var index = Preferences.Columns.ToList().FindIndex(c => c.Column == column.Column);
                    button.IsEnabled = index + offset >= 0 && index + offset < Preferences.Columns.Count;
                    button.Click += (_, _) => { var columns = Preferences.Columns.ToList(); var old = columns.FindIndex(c => c.Column == column.Column); (columns[old], columns[old + offset]) = (columns[old + offset], columns[old]); Preferences.UpdateLayout(Preferences.Layout with { Columns = columns }); ScheduleLayoutSave(); Refresh(); };
                    row.Children.Add(button);
                }
                panel.Children.Add(row);
            }
        }
        Refresh(); await ShowDialogAsync(new ContentDialog { Title = "Transfer columns", Content = panel, CloseButtonText = "Close" });
    }
}
