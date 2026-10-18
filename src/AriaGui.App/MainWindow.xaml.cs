using AriaGui.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using AriaGui.Application.Downloads;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using AriaGui.App.Services;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AriaGui.App;

public sealed partial class MainWindow : Window
{
    private const string FullSubtitle = "Close to tray · Exit to stop";
    private const string ShortSubtitle = "Close to tray";
    private bool _titleRootSubscribed;
    private bool _enforcingMinimumSize;
    public event Action? ExitRequested;
    public void SetDesktopStatus(string message) { DesktopStatus.Text = message; DesktopStatus.Visibility = Visibility.Visible; }
    public void SetTheme(AppTheme theme) => ((FrameworkElement)Content).RequestedTheme = theme switch
        { AppTheme.Light => ElementTheme.Light, AppTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
    public void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        ClampRestoredWindow();
        Activate();
    }
    public MainWindow(DownloadsPage page)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        WindowRoot.SizeChanged += (_, _) => { UpdateTitleSubtitle(); EnsureMinimumWindowSize(); };
        WindowRoot.Loaded += (_, _) =>
        {
            UpdateTitleSubtitle();
            if (!_titleRootSubscribed && WindowRoot.XamlRoot is { } root)
            {
                root.Changed += (_, _) => UpdateTitleSubtitle();
                _titleRootSubscribed = true;
            }
        };
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(1280, Math.Max(1, workArea.Width - 48)), Math.Min(800, Math.Max(1, workArea.Height - 48))));
        ClampRestoredWindow();
#if DEBUG
        // A copied synthetic-profile build may request physical startup dimensions for
        // repeatable compact-layout checks. Normal Debug and all Release runs ignore it.
        if (AppDataPaths.VerificationDirectory is not null)
        {
            void ResizeVerificationWindow()
            {
                var marker = System.IO.Path.Combine(AppContext.BaseDirectory, "r2-fixture-window-pixels.txt");
                if (!System.IO.File.Exists(marker)) return;
                var parts = System.IO.File.ReadAllText(marker).Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height)
                    && width is >= 600 && height is >= 500 && width <= workArea.Width && height <= workArea.Height)
                {
                    AppWindow.Resize(new SizeInt32(width, height));
                    ClampRestoredWindow();
                }
            }
            ResizeVerificationWindow();
            // Allows a fixture resize without clicking the caption and dismissing a flyout.
            // The accelerator exists only in synthetic Debug sessions, never in Release.
            var resize = new KeyboardAccelerator { Key = VirtualKey.R,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
            resize.Invoked += (_, args) => { ResizeVerificationWindow(); args.Handled = true; };
            WindowRoot.KeyboardAccelerators.Add(resize);
        }
#endif
        PageHost.Content = page;
        page.ExitRequested += () => ExitRequested?.Invoke();
    }
    private void ClampRestoredWindow()
    {
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var bounds = RestoredWindowPlacement.Clamp(new(position.X, position.Y, size.Width, size.Height),
            new(workArea.X, workArea.Y, workArea.Width, workArea.Height));
        if (bounds != new WindowRectangle(position.X, position.Y, size.Width, size.Height))
            AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }
    private void EnsureMinimumWindowSize()
    {
        if (_enforcingMinimumSize) return;
        var scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        var minimumWidth = (int)Math.Ceiling(560 * scale);
        var minimumHeight = (int)Math.Ceiling(420 * scale);
        var size = AppWindow.Size;
        if (size.Width >= minimumWidth && size.Height >= minimumHeight) return;
        _enforcingMinimumSize = true;
        AppWindow.Resize(new SizeInt32(Math.Max(size.Width, minimumWidth), Math.Max(size.Height, minimumHeight)));
        _enforcingMinimumSize = false;
    }
    private void UpdateTitleSubtitle()
    {
        // TitleBar owns caption hit testing. Reserve its actual system-button insets in DIPs
        // plus the measured app title and normal title/subtitle spacing.
        var scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        var captionWidth = (AppWindow.TitleBar.LeftInset + AppWindow.TitleBar.RightInset) / scale;
        double Measure(string text)
        {
            var label = new TextBlock { Text = text, FontSize = AppTitleBar.FontSize,
                FontFamily = AppTitleBar.FontFamily };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return label.DesiredSize.Width;
        }
        var available = Math.Max(0, WindowRoot.ActualWidth - captionWidth - Measure("AriaGUI") - 80);
        var mode = ResponsiveShellLayoutPolicy.ResolveTitleSubtitle(available,
            Measure(FullSubtitle), Measure(ShortSubtitle));
        if (mode == TitleSubtitleMode.Full &&
            ResponsiveShellLayoutPolicy.Resolve(WindowRoot.ActualWidth) == ShellLayoutMode.Compact)
            mode = TitleSubtitleMode.Short;
        var subtitle = mode switch { TitleSubtitleMode.Full => FullSubtitle,
            TitleSubtitleMode.Short => ShortSubtitle, _ => string.Empty };
        if (AppTitleBar.Subtitle != subtitle) AppTitleBar.Subtitle = subtitle;
        ToolTipService.SetToolTip(AppTitleBar, FullSubtitle);
    }
    public void DisableInput()
    {
        if (PageHost.Content is DownloadsPage page) page.PrepareForExit();
        PageHost.IsEnabled = false;
    }
}
