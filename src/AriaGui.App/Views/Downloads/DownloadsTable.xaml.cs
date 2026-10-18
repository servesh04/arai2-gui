using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using AriaGui.App.ViewModels;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AriaGui.App.Views.Downloads;

public sealed partial class DownloadsTable : UserControl
{
    public static readonly DependencyProperty IsEngineReadyProperty = DependencyProperty.Register(
        nameof(IsEngineReady), typeof(bool), typeof(DownloadsTable), new PropertyMetadata(true, OnEngineReadyChanged));
    public bool IsEngineReady { get => (bool)GetValue(IsEngineReadyProperty); set => SetValue(IsEngineReadyProperty, value); }

    private const int DateColumnKey = 100;
    private const double DateColumnWidth = 132;
    private DownloadsViewModel ViewModel = null!;
    private PreferencesViewModel Preferences = null!;
    private Action ScheduleLayoutSave = null!;
    private Action RefreshSelectionChecks = null!;
    private readonly Dictionary<Grid, int[]> _cellColumns = [];
    private readonly HashSet<Grid> _rowRoots = [];
    private readonly HashSet<Grid> _pointerRows = [];
    private readonly ObservableCollection<DownloadItemViewModel> _displayItems = [];
    private readonly HashSet<DownloadItemViewModel> _observedItems = [];
    private string _headerLayout = "";
    private DownloadTableSizingMode _tableSizingMode = DownloadTableSizingMode.Fit;
    private IReadOnlyList<DownloadColumnLayout> _resolvedColumns = [];
    private IReadOnlyList<DisplayColumn> _displayColumns = [];
    private double _tableLeadingInset = 32;
    private double _tableTrailingInset;
    private bool _showSizeColumn;
    private bool _isNarrow;
    private bool _syncingDisplay;
    private bool _contextSelecting;
    private bool _contextChangedSelection;
    private bool _dateSortActive;
    private bool _dateSortDescending = true;
    private bool _engineReady = true;
    private IReadOnlyList<Guid> _selectionBeforeContext = [];
    private MenuFlyout? _openContextMenu;
    private ListViewItem? _contextContainer;
    private CheckBox? _headerCheckBox;

    private sealed record DisplayColumn(int Key, string Label, double Width, DownloadSortColumn? ModelColumn = null);

    public DownloadsTable()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => MeasureTableInsets();
    }

    public ListView List => TransferList;
    public ScrollViewer Scroller => TableScroller;
    public Grid Canvas => TableCanvas;
    public Grid Header => TableHeader;
    public IReadOnlyDictionary<Grid, int[]> RegisteredRows => _cellColumns;
    public event SelectionChangedEventHandler? SelectionChanged;
    public event RightTappedEventHandler? RowRightTapped { add { } remove { } }
    public event EventHandler<CheckBox>? RowCheckReady;
    public event EventHandler? RealizedContainersChanged;

    private static void OnEngineReadyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var table = (DownloadsTable)sender;
        var ready = (bool)args.NewValue;
        table._engineReady = ready;
        table.TransferList.IsEnabled = ready;
        table.TransferList.Opacity = ready ? 1 : 0.62;
        foreach (var row in table._cellColumns.Keys) table.ApplyEngineVisualState(row);
    }

    public void Initialize(DownloadsViewModel viewModel, PreferencesViewModel preferences, Action scheduleLayoutSave, Action refreshSelectionChecks)
    {
        ViewModel = viewModel;
        Preferences = preferences;
        ScheduleLayoutSave = scheduleLayoutSave;
        RefreshSelectionChecks = refreshSelectionChecks;
        TransferList.ItemsSource = _displayItems;
        ViewModel.Downloads.CollectionChanged += DownloadsChanged;
        SyncObservedItems();
        SyncDisplayItems();
    }

    public void ApplyLayout()
    {
        _tableSizingMode = Preferences.Layout.TableSizingMode;
        var signature = string.Join(";", Preferences.Columns.Select(c => $"{c.Column}:{c.Visible}:{c.Width:0.##}"))
            + $";size:{_showSizeColumn};narrow:{_isNarrow};date:{_dateSortActive}:{_dateSortDescending};sort:{ViewModel.SortColumn}:{ViewModel.SortDescending}";
        if (_headerLayout != signature) _headerLayout = signature;
        ResolveTableWidths();
        foreach (var grid in _cellColumns.Keys.ToArray()) ConfigureRow(grid);
        UpdateRealizedContainers();
    }

    private void DownloadsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        SyncObservedItems();
        SyncDisplayItems();
    }

    private void SyncObservedItems()
    {
        var current = ViewModel.Downloads.ToHashSet();
        foreach (var item in _observedItems.Where(item => !current.Contains(item)).ToArray())
        {
            item.PropertyChanged -= DownloadItemChanged;
            _observedItems.Remove(item);
        }
        foreach (var item in current.Where(item => !_observedItems.Contains(item)))
        {
            item.PropertyChanged += DownloadItemChanged;
            _observedItems.Add(item);
        }
    }

    private void DownloadItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not DownloadItemViewModel item) return;
        if (args.PropertyName is nameof(DownloadItemViewModel.Entry) or nameof(DownloadItemViewModel.RawStatus))
            SyncDisplayItems();
        if (TransferList.ContainerFromItem(item) is ListViewItem container)
        {
            AutomationProperties.SetName(container, AccessibleName(item));
            container.MinHeight = RowHeight(item);
        }
    }

    private void SyncDisplayItems()
    {
        if (_syncingDisplay) return;
        _syncingDisplay = true;
        try
        {
            IEnumerable<DownloadItemViewModel> source = ViewModel.Downloads;
            if (_dateSortActive)
                source = _dateSortDescending
                    ? source.OrderByDescending(item => item.Entry.Job.CreatedAt).ThenBy(item => item.Id)
                    : source.OrderBy(item => item.Entry.Job.CreatedAt).ThenBy(item => item.Id);
            else if (ViewModel.SortColumn == DownloadSortColumn.Default)
                source = source.OrderBy(item => StatusRank(item.RawStatus))
                    .ThenByDescending(item => item.Entry.Job.CreatedAt).ThenBy(item => item.Id);

            var ordered = source.ToArray();
            for (var i = _displayItems.Count - 1; i >= 0; i--)
                if (!ordered.Contains(_displayItems[i])) _displayItems.RemoveAt(i);
            for (var index = 0; index < ordered.Length; index++)
            {
                var item = ordered[index];
                var old = _displayItems.IndexOf(item);
                if (old < 0) _displayItems.Insert(index, item);
                else if (old != index) _displayItems.Move(old, index);
            }
        }
        finally { _syncingDisplay = false; }
    }

    private static int StatusRank(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading => 0,
        DownloadStatus.Queued => 1,
        DownloadStatus.Paused => 2,
        _ => 3
    };

    private void TableViewportChanged(object sender, SizeChangedEventArgs args) => ResolveTableWidths();

    private void TableViewportViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_openContextMenu is not null) _openContextMenu.Hide();
        ResolveTableWidths();
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        return null;
    }

    private void MeasureTableInsets()
    {
        if (_isNarrow || TableCanvas.ActualWidth <= 0 || TransferList.ActualHeight <= 0) return;
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
        var narrow = viewport < 800;
        if (_isNarrow != narrow)
        {
            _isNarrow = narrow;
            _headerLayout = "";
        }
        if (_isNarrow)
        {
            HeaderRow.Height = new GridLength(0);
            TableHeader.Visibility = Visibility.Collapsed;
            TableScroller.HorizontalScrollMode = ScrollMode.Disabled;
            TableScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            TableScroller.Padding = new Thickness(0);
            TransferList.Margin = new Thickness(0);
            TableCanvas.Width = viewport;
            _displayColumns = [];
            foreach (var grid in _cellColumns.Keys.ToArray()) ConfigureRow(grid);
            return;
        }

        HeaderRow.Height = new GridLength(30);
        TableHeader.Visibility = Visibility.Visible;
        TableScroller.HorizontalScrollMode = ScrollMode.Enabled;
        TableScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        var reserveForScrollbar = _tableSizingMode == DownloadTableSizingMode.Manual ? 12 : 0;
        TransferList.Margin = new Thickness(0, 0, 0, reserveForScrollbar);

        var dataColumns = Preferences.Columns
            .Where(column => column.Visible && (column.Column != DownloadSortColumn.Size || _showSizeColumn))
            .ToArray();
        var availableForData = Math.Max(0, viewport - DateColumnWidth);
        var resolved = DownloadTableWidthResolver.Resolve(dataColumns, availableForData,
            _tableLeadingInset + _tableTrailingInset, _tableSizingMode);
        _resolvedColumns = resolved.Columns;
        _displayColumns = dataColumns
            .Select(column => new DisplayColumn((int)column.Column, ColumnLabel(column.Column), ResolvedWidth(column.Column), column.Column))
            .Append(new DisplayColumn(DateColumnKey, "Date added", DateColumnWidth))
            .ToArray();
        TableCanvas.Width = resolved.CanvasWidth + DateColumnWidth;
        TableHeader.Padding = new Thickness(_tableLeadingInset, 0, _tableTrailingInset, 0);
        BuildHeader();
        RefreshRealizedRows();
        foreach (var grid in _cellColumns.Keys.ToArray()) ConfigureRow(grid);
    }

    private double ResolvedWidth(DownloadSortColumn column) => _resolvedColumns.FirstOrDefault(c => c.Column == column)?.Width ?? 0;

    private void RowLoaded(object sender, RoutedEventArgs args) => RegisterRowGrid((Grid)sender);

    private void RegisterRowGrid(Grid grid)
    {
        if (!_cellColumns.ContainsKey(grid))
        {
            _cellColumns[grid] = grid.Children.Cast<FrameworkElement>().Select(cell => System.Convert.ToInt32(cell.Tag)).ToArray();
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

    private void TransferRowLoaded(object sender, RoutedEventArgs args)
    {
        var root = (Grid)sender;
        if (_rowRoots.Add(root)) root.Unloaded += TransferRowUnloaded;
        UpdateRowActionVisibility(root);
    }

    private void TransferRowUnloaded(object sender, RoutedEventArgs args)
    {
        var root = (Grid)sender;
        _rowRoots.Remove(root);
        _pointerRows.Remove(root);
        root.Unloaded -= TransferRowUnloaded;
    }

    private void TransferRowPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _pointerRows.Add((Grid)sender);
        UpdateRowActionVisibility((Grid)sender);
    }

    private void TransferRowPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointerRows.Remove((Grid)sender);
        UpdateRowActionVisibility((Grid)sender);
    }

    private static FrameworkElement? FindTagged(DependencyObject parent, string tag)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && Equals(element.Tag, tag)) return element;
            if (FindTagged(child, tag) is { } descendant) return descendant;
        }
        return null;
    }

    private void UpdateRowActionVisibility(Grid root)
    {
        if (FindTagged(root, "HoverActions") is not FrameworkElement actions) return;
        var selected = root.DataContext is DownloadItemViewModel item && TransferList.SelectedItems.Contains(item);
        actions.Visibility = (_pointerRows.Contains(root) || selected) && !_isNarrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshRealizedRows()
    {
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
        var root = VisualTreeHelper.GetParent(grid) as Grid;
        var narrowRow = root is null ? null : FindTagged(root, "NarrowRow") as FrameworkElement;
        grid.Visibility = _isNarrow ? Visibility.Collapsed : Visibility.Visible;
        if (narrowRow is not null) narrowRow.Visibility = _isNarrow ? Visibility.Visible : Visibility.Collapsed;
        if (_isNarrow)
        {
            grid.Width = Math.Max(0, TableCanvas.Width - _tableLeadingInset - _tableTrailingInset);
            if (root is not null) root.MinHeight = root.DataContext is DownloadItemViewModel narrowItem && narrowItem.RawStatus == DownloadStatus.Failed ? 66 : 56;
            ApplyEngineVisualState(grid);
            return;
        }

        grid.ColumnDefinitions.Clear();
        foreach (var column in _displayColumns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
        grid.Width = _displayColumns.Sum(column => column.Width);
        for (var i = 0; i < grid.Children.Count; i++)
        {
            var cell = (FrameworkElement)grid.Children[i];
            var index = _displayColumns.ToList().FindIndex(column => column.Key == original[i]);
            cell.Visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (index >= 0) Grid.SetColumn(cell, index);
        }
        var item = grid.DataContext as DownloadItemViewModel;
        grid.MinHeight = item is not null ? RowHeight(item) : Preferences.CompactRows ? 32 : 40;
        if (root is not null) root.MinHeight = grid.MinHeight;
        ApplyEngineVisualState(grid);
    }

    private void ApplyEngineVisualState(Grid grid)
    {
        if (grid.Children.OfType<TextBlock>().FirstOrDefault(element => element.Tag is "5") is not { } speed) return;
        if (!_engineReady) speed.Text = "—";
        else speed.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(DownloadItemViewModel.SpeedLabel)) });
    }

    private double RowHeight(DownloadItemViewModel item) => item.RawStatus == DownloadStatus.Failed
        ? 58
        : Preferences.CompactRows ? 32 : 40;

    private static string ColumnLabel(DownloadSortColumn column) => column switch
    {
        DownloadSortColumn.Speed => "Speed",
        DownloadSortColumn.Eta => "ETA",
        _ => column.ToString()
    };

    private void BuildHeader()
    {
        TableHeader.Children.Clear();
        TableHeader.ColumnDefinitions.Clear();
        for (var i = 0; i < _displayColumns.Count; i++)
        {
            var column = _displayColumns[i];
            TableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
            var arrow = column.Key == DateColumnKey && _dateSortActive
                ? (_dateSortDescending ? " ↓" : " ↑")
                : column.ModelColumn is { } model && !_dateSortActive && ViewModel.SortColumn == model && model != DownloadSortColumn.Default
                    ? (ViewModel.SortDescending ? " ↓" : " ↑")
                    : "";
            var button = new Button
            {
                Content = column.Label + arrow,
                Padding = new Thickness(8, 0, 12, 0),
                MinHeight = 28,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = column.ModelColumn is DownloadSortColumn.Speed or DownloadSortColumn.Eta or DownloadSortColumn.Size
                    ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                BorderThickness = new Thickness(0),
                Tag = column
            };
            button.Click += HeaderClick;
            Grid.SetColumn(button, i);
            TableHeader.Children.Add(button);
            if (column.ModelColumn is not { } modelColumn) continue;
            var grip = new Thumb
            {
                Width = 7,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsTabStop = true,
                UseSystemFocusVisuals = true,
                Tag = modelColumn
            };
            grip.AddHandler(PointerPressedEvent, new PointerEventHandler(GripPointerPressed), true);
            SetGripAutomation(grip, $"{ColumnLabel(modelColumn)} column width", Preferences.Columns.First(c => c.Column == modelColumn).Width,
                DesktopLayout.MinimumColumnWidth, DesktopLayout.MaximumColumnWidth, "left and right");
            grip.DragDelta += (_, e) => ResizeColumn(modelColumn, e.HorizontalChange);
            grip.KeyDown += (_, e) =>
            {
                if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
                ResizeColumn(modelColumn, e.Key == VirtualKey.Right ? 10 : -10);
                e.Handled = true;
            };
            Grid.SetColumn(grip, i);
            TableHeader.Children.Add(grip);
        }

        _headerCheckBox = new CheckBox
        {
            IsThreeState = true,
            MinWidth = 0,
            MinHeight = 0,
            Margin = new Thickness(-28, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(_headerCheckBox, "Select all visible downloads");
        _headerCheckBox.Click += HeaderCheckClick;
        if (TableHeader.ColumnDefinitions.Count > 0) Grid.SetColumn(_headerCheckBox, 0);
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_headerCheckBox, 10);
        TableHeader.Children.Add(_headerCheckBox);
        UpdateHeaderSelection();
        TableHeader.ContextFlyout = CreateColumnMenu();
    }

    private void HeaderClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not DisplayColumn column) return;
        if (column.Key == DateColumnKey)
        {
            if (_dateSortActive) _dateSortDescending = !_dateSortDescending;
            else { _dateSortActive = true; _dateSortDescending = true; }
            SyncDisplayItems();
        }
        else if (column.ModelColumn is { } model)
        {
            _dateSortActive = false;
            ViewModel.SetSort(model);
            SyncDisplayItems();
        }
        _headerLayout = "";
        ResolveTableWidths();
    }

    private MenuFlyout CreateColumnMenu()
    {
        var menu = new MenuFlyout();
        foreach (var column in new[]
                 {
                     DownloadSortColumn.Name, DownloadSortColumn.Progress, DownloadSortColumn.Status,
                     DownloadSortColumn.Speed, DownloadSortColumn.Eta, DownloadSortColumn.Size
                 })
        {
            var preference = Preferences.Columns.First(item => item.Column == column);
            var entry = new ToggleMenuFlyoutItem
            {
                Text = ColumnLabel(column),
                IsChecked = column == DownloadSortColumn.Size ? _showSizeColumn : preference.Visible,
                IsEnabled = column != DownloadSortColumn.Name,
                Tag = column
            };
            entry.Click += ColumnMenuClick;
            menu.Items.Add(entry);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new ToggleMenuFlyoutItem { Text = "Date added", IsChecked = true, IsEnabled = false });
        return menu;
    }

    private void ColumnMenuClick(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleMenuFlyoutItem entry || entry.Tag is not DownloadSortColumn column) return;
        if (column == DownloadSortColumn.Size) _showSizeColumn = entry.IsChecked;
        else
        {
            Preferences.UpdateLayout(Preferences.Layout with
            {
                Columns = Preferences.Columns.Select(item => item.Column == column ? item with { Visible = entry.IsChecked } : item).ToArray()
            });
            ScheduleLayoutSave();
        }
        _headerLayout = "";
        ResolveTableWidths();
    }

    private void ResizeColumn(DownloadSortColumn column, double delta)
    {
        var columns = Preferences.Columns.Select(c => c.Visible
            ? c with { Width = Math.Clamp(ResolvedWidth(c.Column), DesktopLayout.MinimumColumnWidth, DesktopLayout.MaximumColumnWidth) }
            : c).ToArray();
        _tableSizingMode = DownloadTableSizingMode.Manual;
        Preferences.UpdateLayout(Preferences.Layout with
        {
            TableSizingMode = DownloadTableSizingMode.Manual,
            Columns = columns.Select(c => c.Column == column
                ? c with { Width = Math.Clamp(ResolvedWidth(column) + delta, DesktopLayout.MinimumColumnWidth, DesktopLayout.MaximumColumnWidth) }
                : c).ToArray()
        });
        ScheduleLayoutSave();
    }

    public static void SetGripAutomation(Thumb grip, string label, double value, double minimum, double maximum, string keys)
    {
        var name = $"Resize {label}, {value:0.#} device-independent pixels; use {keys} arrows";
        var status = $"Preferred size {value:0.#}; range {minimum:0.#} to {maximum:0.#} device-independent pixels";
        if (AutomationProperties.GetName(grip) != name) AutomationProperties.SetName(grip, name);
        if (AutomationProperties.GetItemStatus(grip) != status) AutomationProperties.SetItemStatus(grip, status);
    }

    private void GripPointerPressed(object sender, PointerRoutedEventArgs args) => ((Thumb)sender).Focus(FocusState.Pointer);

    private void TransferSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        UpdateHeaderSelection();
        foreach (var root in _rowRoots) UpdateRowActionVisibility(root);
        if (_contextSelecting) return;
        if (_openContextMenu is not null) _openContextMenu.Hide();
        SelectionChanged?.Invoke(sender, args);
    }

    private void HeaderCheckClick(object sender, RoutedEventArgs args)
    {
        if (_headerCheckBox?.IsChecked == true) TransferList.SelectAll();
        else TransferList.SelectedItems.Clear();
        UpdateHeaderSelection();
    }

    private void UpdateHeaderSelection()
    {
        if (_headerCheckBox is null) return;
        _headerCheckBox.IsChecked = TransferList.SelectedItems.Count == 0
            ? false
            : TransferList.SelectedItems.Count == TransferList.Items.Count ? true : null;
    }

    private void DownloadContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            AutomationProperties.SetName(args.ItemContainer, "");
            args.ItemContainer.ContextFlyout = null;
            return;
        }
        if (args.Item is not DownloadItemViewModel item || args.ItemContainer is not ListViewItem container) return;
        AutomationProperties.SetName(container, AccessibleName(item));
        container.MinHeight = RowHeight(item);
        var menu = CreateRowMenu(item);
        menu.Opening += (_, _) => PrepareContextMenu(container, item, menu);
        container.ContextFlyout = menu;
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshRealizedRows();
            RealizedContainersChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private static string AccessibleName(DownloadItemViewModel item)
    {
        var status = item.RawStatus switch
        {
            DownloadStatus.Queued => "Waiting",
            DownloadStatus.Removed => "Stopped",
            _ => item.RawStatus.ToString()
        };
        return $"{item.FileName}, {status}, {item.ProgressLabel}, {item.SpeedLabel}";
    }

    private void PrepareContextMenu(ListViewItem container, DownloadItemViewModel item, MenuFlyout menu)
    {
        if (_openContextMenu is not null && !ReferenceEquals(_openContextMenu, menu)) _openContextMenu.Hide();
        _selectionBeforeContext = TransferList.SelectedItems.Cast<DownloadItemViewModel>().Select(selected => selected.Id).ToArray();
        _contextChangedSelection = !TransferList.SelectedItems.Contains(item);
        if (_contextChangedSelection)
        {
            _contextSelecting = true;
            try
            {
                TransferList.SelectedItems.Clear();
                TransferList.SelectedItems.Add(item);
            }
            finally { _contextSelecting = false; }
        }
        _openContextMenu = menu;
        _contextContainer = container;
        menu.Closed -= RowMenuClosed;
        menu.Closed += RowMenuClosed;
    }

    private void RowMenuClosed(object? sender, object args)
    {
        if (_contextChangedSelection)
        {
            _contextSelecting = true;
            try
            {
                TransferList.SelectedItems.Clear();
                foreach (var item in _displayItems.Where(item => _selectionBeforeContext.Contains(item.Id)))
                    TransferList.SelectedItems.Add(item);
            }
            finally { _contextSelecting = false; }
        }
        _contextContainer?.Focus(FocusState.Programmatic);
        _openContextMenu = null;
        _contextContainer = null;
        _contextChangedSelection = false;
        _selectionBeforeContext = [];
        UpdateHeaderSelection();
    }

    private MenuFlyout CreateRowMenu(DownloadItemViewModel item)
    {
        var menu = new MenuFlyout();
        if (item.ShowRetry) menu.Items.Add(CommandItem("Retry", item.RetryCommand));
        else if (item.ShowPause) menu.Items.Add(CommandItem("Pause", item.PauseCommand));
        else if (item.ShowResume) menu.Items.Add(CommandItem("Resume", item.ResumeCommand));
        if (item.ShowOpenFile) menu.Items.Add(CommandItem("Open file", item.OpenFileCommand));
        if (item.OpenFolderCommand.CanExecute(null)) menu.Items.Add(CommandItem("Open folder", item.OpenFolderCommand));
        var copy = new MenuFlyoutItem { Text = "Copy link", Tag = item };
        copy.Click += CopyLinkClick;
        menu.Items.Add(copy);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CommandItem("Remove from list", item.RemoveCommand));
        var delete = new MenuFlyoutItem { Text = "Delete files from disk…", IsEnabled = false };
        ToolTipService.SetToolTip(delete, "File deletion is unavailable in this build.");
        menu.Items.Add(delete);
        return menu;
    }

    private static MenuFlyoutItem CommandItem(string text, System.Windows.Input.ICommand command) =>
        new() { Text = text, Command = command };

    private void CopyLinkClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadItemViewModel item) return;
        var package = new DataPackage();
        package.SetText(item.SourceUrl);
        Clipboard.SetContent(package);
    }

    private void RowOverflowClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || button.DataContext is not DownloadItemViewModel item) return;
        if (!TransferList.SelectedItems.Contains(item))
        {
            TransferList.SelectedItems.Clear();
            TransferList.SelectedItems.Add(item);
        }
        var menu = CreateRowMenu(item);
        _openContextMenu = menu;
        _contextContainer = TransferList.ContainerFromItem(item) as ListViewItem;
        menu.Closed += RowMenuClosed;
        menu.ShowAt(button);
    }

    private async void TransferListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Space)
        {
            var selected = TransferList.SelectedItems.Cast<DownloadItemViewModel>().ToArray();
            if (selected.Length == 1)
            {
                var item = selected[0];
                var command = item.PauseCommand.CanExecute(null) ? item.PauseCommand : item.ResumeCommand;
                if (command.CanExecute(null)) command.Execute(null);
            }
            else
            {
                var command = ViewModel.BatchPauseCommand.CanExecute(null) ? ViewModel.BatchPauseCommand : ViewModel.BatchResumeCommand;
                if (command.CanExecute(null)) command.Execute(null);
            }
            args.Handled = true;
            return;
        }
        if (args.Key == VirtualKey.Delete)
        {
            args.Handled = true;
            await ConfirmRemoveSelectionAsync();
            return;
        }
        if (args.Key == VirtualKey.Enter && TransferList.SelectedItem is not null)
        {
            args.Handled = true;
            TransferList.StartBringIntoView();
        }
    }

    private async Task ConfirmRemoveSelectionAsync()
    {
        if (TransferList.SelectedItems.Count == 0 || !ViewModel.BatchRemoveCommand.CanExecute(null)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = TransferList.SelectedItems.Count == 1 ? "Remove download?" : $"Remove {TransferList.SelectedItems.Count} downloads?",
            Content = "The entries will be removed from the transfer list. Downloaded files are kept.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && ViewModel.BatchRemoveCommand.CanExecute(null))
            ViewModel.BatchRemoveCommand.Execute(null);
    }

    private void RowCheckLoaded(object sender, RoutedEventArgs args) => RowCheckReady?.Invoke(this, (CheckBox)sender);
    private void RowCheckTapped(object sender, TappedRoutedEventArgs args) => args.Handled = true;
    private void RowCheckClick(object sender, RoutedEventArgs args) => RefreshSelectionChecks();

    private void UpdateRealizedContainers()
    {
        foreach (var item in _displayItems)
            if (TransferList.ContainerFromItem(item) is ListViewItem container) container.MinHeight = RowHeight(item);
    }

    public async Task ShowColumnsAsync(Func<ContentDialog, Task> showDialog)
    {
        var panel = new StackPanel { Spacing = 8 };
        void Refresh()
        {
            panel.Children.Clear();
            var sizing = new ComboBox
            {
                Header = "Column sizing",
                ItemsSource = new[] { "Fit viewport", "Manual widths" },
                SelectedIndex = _tableSizingMode == DownloadTableSizingMode.Fit ? 0 : 1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            sizing.SelectionChanged += (_, _) =>
            {
                Preferences.UpdateLayout(Preferences.Layout with
                {
                    TableSizingMode = sizing.SelectedIndex == 0 ? DownloadTableSizingMode.Fit : DownloadTableSizingMode.Manual
                });
                ScheduleLayoutSave();
            };
            panel.Children.Add(sizing);
            foreach (var column in new[]
                     {
                         DownloadSortColumn.Name, DownloadSortColumn.Progress, DownloadSortColumn.Status,
                         DownloadSortColumn.Speed, DownloadSortColumn.Eta, DownloadSortColumn.Size
                     })
            {
                var preference = Preferences.Columns.First(item => item.Column == column);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var check = new CheckBox
                {
                    Content = ColumnLabel(column),
                    IsChecked = column == DownloadSortColumn.Size ? _showSizeColumn : preference.Visible,
                    Width = 160,
                    IsEnabled = column != DownloadSortColumn.Name
                };
                check.Click += (_, _) =>
                {
                    if (column == DownloadSortColumn.Size) _showSizeColumn = check.IsChecked == true;
                    else
                    {
                        Preferences.UpdateLayout(Preferences.Layout with
                        {
                            Columns = Preferences.Columns.Select(item => item.Column == column
                                ? item with { Visible = check.IsChecked == true } : item).ToArray()
                        });
                        ScheduleLayoutSave();
                    }
                    _headerLayout = "";
                    ResolveTableWidths();
                };
                row.Children.Add(check);
                panel.Children.Add(row);
            }
            var date = new CheckBox { Content = "Date added", IsChecked = true, IsEnabled = false, Width = 160 };
            panel.Children.Add(date);
        }
        Refresh();
        await showDialog(new ContentDialog { Title = "Transfer columns", Content = panel, CloseButtonText = "Close" });
    }
}
