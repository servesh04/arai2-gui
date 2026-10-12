using AriaGui.App.Services;
using AriaGui.App.ViewModels;
using AriaGui.App.Views;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Persistence;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Browser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

namespace AriaGui.App;

public partial class App : Microsoft.UI.Xaml.Application, IDesktopLifetimeHost
{
    private MainWindow? _window;
    private ServiceProvider? _services;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _updates;
    private Task? _engineLoop;
    private Task? _preferencesLoad;
    private SingleInstanceLease? _instance;
    private bool _closing;
    private bool _allowClose;
    private Win32TrayIcon? _tray;
    private DesktopLifetimeController? _desktop;
    private BrowserBridgeServer? _browserBridge;
    private Task? _browserStart = null;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var runtimeDirectory = AppDataPaths.RuntimeDirectory;
        EngineException? storageFailure = null;
        try
        {
            _instance = new SingleInstanceLease(runtimeDirectory);
            if (!_instance.IsPrimary) { Win32TrayIcon.RequestExistingWindow(); _instance.Dispose(); Exit(); return; }
        }
        catch (EngineException ex) { storageFailure = ex; }

        // Composition root: one owned engine supplies lifecycle and real download RPC.
        var registrations = new ServiceCollection();
        registrations.AddSingleton(TimeProvider.System);
        registrations.AddSingleton<IUiDispatcher>(new WinUiDispatcher(DispatcherQueue.GetForCurrentThread()));
        registrations.AddSingleton<IAria2Client>(services => new ManagedAria2Client((Aria2ProcessManager)services.GetRequiredService<IManagedAria2Engine>()));
        registrations.AddSingleton<IDownloadRepository>(new SqliteDownloadRepository(Path.Combine(runtimeDirectory, "downloads.db")));
        registrations.AddSingleton<IDownloadDestination, FileDownloadDestination>();
        registrations.AddSingleton<IDownloadFileActions, WindowsDownloadFileActions>();
        registrations.AddSingleton<IUserPreferencesRepository>(new JsonUserPreferencesRepository(Path.Combine(runtimeDirectory, "preferences.json")));
        registrations.AddSingleton<IBrowserIntegrationDiagnostics>(new BrowserIntegrationDiagnostics());
        registrations.AddSingleton<PreferencesViewModel>();
        var folderPicker = new WinUiFolderPicker();
        registrations.AddSingleton<IDownloadFolderPicker>(folderPicker);
        registrations.AddSingleton<DownloadRuntimeStore>();
        registrations.AddSingleton<DownloadCoordinator>();
        registrations.AddSingleton(services => new DownloadPreferencesService(services.GetRequiredService<IUserPreferencesRepository>(), DownloadPreferencesService.DefaultFallbackFolder));
        registrations.AddSingleton(services => new ExternalDownloadIntakeService(services.GetRequiredService<DownloadCoordinator>(),
            services.GetRequiredService<DownloadPreferencesService>(), TimeProvider.System, _lifetime.Token,
            new EngineDispatchReadiness(services.GetRequiredService<EngineStateStore>())));
        registrations.AddSingleton<DownloadsViewModel>();
        registrations.AddSingleton<DownloadSummaryViewModel>();
        registrations.AddSingleton(new Aria2ProcessOptions(AppDataPaths.EngineExecutable, runtimeDirectory));
        registrations.AddSingleton<IManagedAria2Engine, Aria2ProcessManager>();
        registrations.AddSingleton<EngineStateStore>();
        registrations.AddSingleton<EngineLifecycleService>();
        registrations.AddSingleton<EngineViewModel>();
        registrations.AddTransient<DownloadsPage>();
        _services = registrations.BuildServiceProvider();

        _window = new MainWindow(_services.GetRequiredService<DownloadsPage>());
        var preferences = _services.GetRequiredService<PreferencesViewModel>();
        preferences.Applied += value =>
        {
            _window.SetTheme(value.Theme);
            _services.GetRequiredService<DownloadsViewModel>().DirectoryPath = string.IsNullOrEmpty(value.DefaultFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AriaGUI") : value.DefaultFolder;
        };
        _preferencesLoad = preferences.LoadAsync(_lifetime.Token);
        folderPicker.WindowId = _window.AppWindow.Id;
        _window.AppWindow.Closing += OnWindowClosing;
        _desktop = new DesktopLifetimeController(this);
        _window.ExitRequested += RequestExit;
        try
        {
            _tray = new Win32TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(_window), DispatcherQueue.GetForCurrentThread(), Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            _tray.OpenRequested += () => _desktop.Open();
            _tray.ExitRequested += RequestExit;
            _tray.Unavailable += () => { _desktop.Open(); _window.SetDesktopStatus("Tray unavailable. The window stays accessible; use Exit to stop."); };
            _tray.Suspending += OnSuspending;
            _tray.Resuming += OnResuming;
            if (!_tray.EnsureRegistered()) _window.SetDesktopStatus("Tray unavailable. Close will keep this window visible; use Exit to stop.");
        }
        catch (Exception) { _window.SetDesktopStatus("Tray unavailable. Close will keep this window visible; use Exit to stop."); }
        _window.Activate();
        _updates = RunDownloadsAsync(_services.GetRequiredService<DownloadCoordinator>(), _services.GetRequiredService<ExternalDownloadIntakeService>(), _lifetime.Token);
        var lifecycle = _services.GetRequiredService<EngineLifecycleService>();
        if (storageFailure is not null) lifecycle.ReportStartupFailure(storageFailure);
        else _engineLoop = RunEngineAsync(lifecycle, _lifetime.Token);
#if DEBUG || ARIAGUI_VERIFICATION
        // No production browser listener. Explicit synthetic profile + trusted peer marker only.
        if (storageFailure is null && _instance is { IsPrimary: true } && AppDataPaths.VerificationDirectory is { } fixture)
        {
            var peerMarker = Path.Combine(fixture, "browser-bridge-synthetic-peer.txt");
            if (File.Exists(peerMarker) && new FileInfo(peerMarker).Length <= 2048)
                _browserStart = StartSyntheticBridgeAsync(runtimeDirectory, File.ReadAllText(peerMarker).Trim());
        }
#endif
    }

    private async Task StartSyntheticBridgeAsync(string runtime, string peerExecutable)
    {
        try
        {
            var services = _services ?? throw new InvalidOperationException("Synthetic bridge composition unavailable.");
            _browserBridge = new(services.GetRequiredService<ExternalDownloadIntakeService>(),
                services.GetRequiredService<DownloadPreferencesService>(), _instance!,
                BrowserBridgeEndpoint.Open(runtime, "verification", "synthetic-gui"), peerExecutable, _lifetime.Token);
            await _browserBridge.StartAsync(_lifetime.Token);
        }
        catch (Exception) { _window?.SetDesktopStatus("Synthetic browser bridge unavailable; no browser work admitted."); }
    }

    private static async Task RunDownloadsAsync(DownloadCoordinator coordinator, ExternalDownloadIntakeService intake, CancellationToken cancellationToken)
    {
        try
        {
            await intake.InitializeAsync(cancellationToken);
            await coordinator.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (DownloadStorageException ex) { await coordinator.ReportFailureAsync(ex.Message); }
        catch (Exception) { await coordinator.ReportFailureAsync("History could not be initialized. Keep the app database for diagnosis."); }
    }

    private static async Task RunEngineAsync(EngineLifecycleService lifecycle, CancellationToken cancellationToken)
    {
        try
        {
            await lifecycle.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (!_closing && _desktop?.CloseToTray() != true)
            _window?.SetDesktopStatus("Tray unavailable. The window remains open; use Exit to stop downloads.");
    }
    bool IDesktopLifetimeHost.EnsureTray() => _tray?.EnsureRegistered() == true;
    void IDesktopLifetimeHost.Hide() => _window?.AppWindow.Hide();
    void IDesktopLifetimeHost.Show() => _window?.ShowFromTray();
    Task IDesktopLifetimeHost.ExitAsync() => ExitOwnedAsync();
    private async void RequestExit()
    {
        if (_desktop is not null) await _desktop.ExitAsync();
    }
    private async void OnSuspending()
    {
        if (_closing || _services is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _services.GetRequiredService<DownloadCoordinator>().CheckpointAsync(deadline.Token);
            await _services.GetRequiredService<EngineLifecycleService>().OnSuspendAsync(deadline.Token);
        }
        catch (Exception) { _window?.SetDesktopStatus("Suspend checkpoint was unavailable; preserved state will be checked on resume."); }
    }
    private async void OnResuming()
    {
        if (_closing || _services is null) return;
        try
        {
            await _services.GetRequiredService<EngineLifecycleService>().OnResumeAsync(_lifetime.Token);
            await _services.GetRequiredService<DownloadCoordinator>().RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_closing) await _services.GetRequiredService<DownloadCoordinator>().ReportFailureAsync("Resume reconnection is pending; the engine monitor will retry safely.");
        }
    }

    // Explicit Exit keeps the window alive until commands unwind and both stores checkpoint.
    private async Task ExitOwnedAsync()
    {
        if (_closing) return;
        _closing = true;
        _browserBridge?.StopAdmission();
        _window?.DisableInput();
        try
        {
            var engineViewModel = _services?.GetRequiredService<EngineViewModel>();
            engineViewModel?.CancelOperations();
            var downloadViewModel = _services?.GetRequiredService<DownloadsViewModel>();
            downloadViewModel?.CancelOperations();
            _services?.GetRequiredService<PreferencesViewModel>().SaveCommand.Cancel();
            _services?.GetRequiredService<PreferencesViewModel>().CheckBrowserCommand.Cancel();
            _lifetime.Cancel();
            if (_browserStart is not null)
            {
                try { await _browserStart.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            }
            if (_browserBridge is not null)
            {
                using var bridgeDrain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _browserBridge.DrainAsync(bridgeDrain.Token); } catch (OperationCanceledException) { }
                await _browserBridge.DisposeAsync();
            }
            if (_updates is not null) await _updates;
            if (_engineLoop is not null) await _engineLoop;
            if (_preferencesLoad is not null) await _preferencesLoad;
            if (_services is not null)
            {
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _services.GetRequiredService<ExternalDownloadIntakeService>().DrainAsync(drain.Token); }
                catch (OperationCanceledException) { /* Durable claims are reconciled on restart; never replay. */ }
            }
            // A retry or manual health check may still be unwinding its cancellation.
            foreach (var operation in new[] { engineViewModel?.StartCommand.ExecutionTask, engineViewModel?.CheckCommand.ExecutionTask,
                _services?.GetRequiredService<PreferencesViewModel>().SaveCommand.ExecutionTask }.OfType<Task>().Concat(downloadViewModel?.PendingTasks ?? []))
            {
                if (operation is null) continue;
                try { await operation; }
                catch (OperationCanceledException) { }
            }
            if (_services is not null)
            {
                // Pane drags use a short save debounce. Flush the last layout after
                // input and that timer stop, before releasing the preferences store.
                await _services.GetRequiredService<PreferencesViewModel>().SaveLayoutAsync();
                try { await _services.GetRequiredService<DownloadCoordinator>().CheckpointAsync(); }
                catch (DownloadStorageException) { _window?.SetDesktopStatus("Final history checkpoint unavailable; existing history is preserved."); }
                await _services.GetRequiredService<EngineLifecycleService>().StopAsync();
                await _services.DisposeAsync();
            }
        }
        catch (Exception) { _window?.SetDesktopStatus("Exit cleanup could not finish. Preserved ownership and checkpoints will be reviewed on the next launch."); }
        finally
        {
            _tray?.Dispose();
            _instance?.Dispose();
            _lifetime.Dispose();
            _allowClose = true;
            _window?.Close();
            Exit();
        }
    }
}
