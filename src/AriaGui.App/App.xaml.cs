using AriaGui.App.Services;
using AriaGui.App.ViewModels;
using AriaGui.App.Views;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Persistence;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Browser;
using AriaGui.App.Features;
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
    private Task? _reservationCleanup;
    private Task? _engineLoop;
    private Task? _preferencesLoad;
    private SingleInstanceLease? _instance;
    private bool _closing;
    private bool _allowClose;
    private Win32TrayIcon? _tray;
    private DesktopLifetimeController? _desktop;
    private BrowserBridgeServer? _browserBridge;
    private BrowserHandoffPipeServer? _handoffPipe;
    private Task? _browserStart = null;
    private OpenActivationController? _openActivation;
    private Win32OpenActivation? _openReceiver;
    private readonly Microsoft.Windows.AppLifecycle.AppInstance _activationInstance;

    public App()
    {
        InitializeComponent();
        // Argument inspection/events only. GUI ownership continues to use SingleInstanceLease.
        _activationInstance = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent();
        _activationInstance.Activated += OnProtocolActivated;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var activation = _activationInstance.GetActivatedEventArgs();
        var supported = IsSupportedActivation(activation);
        WriteSyntheticActivationReceipt(activation, supported);
        if (!supported) { Exit(); return; }
        try
        {
            var registered = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("AriaGUI.Primary");
            if (!registered.IsCurrent)
            {
                _ = RedirectToPrimaryAsync(registered, activation);
                return;
            }
        }
        catch { /* The existing single-instance lease remains the ownership fallback. */ }
        var runtimeDirectory = AppDataPaths.RuntimeDirectory;
        EngineException? storageFailure = null;
        try
        {
            _instance = new SingleInstanceLease(runtimeDirectory);
            if (!_instance.IsPrimary) { Win32OpenActivation.RequestExistingWindow(runtimeDirectory); _instance.Dispose(); Exit(); return; }
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
        // Features stay gated until private-profile process/RPC verification and the integration-phase UI wiring are complete.
        registrations.AddSingleton(FeatureFlags.BackendImplemented);
        registrations.AddSingleton<IEngineControl, EngineControlService>();
        registrations.AddSingleton<ISpeedLimitService, SpeedLimitService>();
        registrations.AddSingleton<IHandoffNotifier, BrowserHandoffNotifier>();
        var folderPicker = new WinUiFolderPicker();
        registrations.AddSingleton<IDownloadFolderPicker>(folderPicker);
        registrations.AddSingleton<DownloadRuntimeStore>();
        registrations.AddSingleton<DownloadCoordinator>();
        // The guarded continuity gateway fetches the relay from this process after aria2 contacts the gateway.
        registrations.AddSingleton<BrowserCookieRelay>(_ => new(() => Environment.ProcessId));
        registrations.AddSingleton<IBrowserHandoffSubmitter, BrowserHandoffSubmitter>();
        registrations.AddSingleton<BrowserHandoffService>();
        registrations.AddSingleton<IBrowserHandoffStatus>(services => services.GetRequiredService<BrowserHandoffService>());
        registrations.AddSingleton(services => new DownloadPreferencesService(services.GetRequiredService<IUserPreferencesRepository>(), DownloadPreferencesService.DefaultFallbackFolder));
        registrations.AddSingleton(services => new ExternalDownloadIntakeService(services.GetRequiredService<DownloadCoordinator>(),
            services.GetRequiredService<DownloadPreferencesService>(), TimeProvider.System, _lifetime.Token,
            new EngineDispatchReadiness(services.GetRequiredService<EngineStateStore>())));
        registrations.AddSingleton<DownloadsViewModel>();
        registrations.AddSingleton<DownloadSummaryViewModel>();
        var engineOptions = Aria2ProcessOptions.ForApplication(AppDataPaths.EngineExecutable, runtimeDirectory);
#if DEBUG || ARIAGUI_VERIFICATION
        if (AppDataPaths.VerificationDirectory is { } engineFixture)
            engineOptions = engineOptions with { RpcPort = SyntheticEngineFixturePort.Read(engineFixture) };
#endif
        registrations.AddSingleton(engineOptions);
        registrations.AddSingleton<IManagedAria2Engine, Aria2ProcessManager>();
        registrations.AddSingleton<EngineStateStore>();
        registrations.AddSingleton<EngineLifecycleService>();
        registrations.AddSingleton<EngineViewModel>();
        registrations.AddTransient<DownloadsPage>();
        _services = registrations.BuildServiceProvider();

        _window = new MainWindow(_services.GetRequiredService<DownloadsPage>());
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _openActivation = new(TimeProvider.System, action => dispatcher.TryEnqueue(action.Invoke), () => _desktop?.Open());
        try { if (_instance is { IsPrimary: true }) _openReceiver = new(WinRT.Interop.WindowNative.GetWindowHandle(_window), runtimeDirectory, _openActivation); }
        catch (Exception) { _window.SetDesktopStatus("Opening an existing window through a link is unavailable; use the tray or taskbar."); }
        var preferences = _services.GetRequiredService<PreferencesViewModel>();
        preferences.HandoffStatus = _services.GetRequiredService<IBrowserHandoffStatus>();
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
        _openActivation.Ready();
        _window.ExitRequested += RequestExit;
        try
        {
            _tray = new Win32TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(_window), DispatcherQueue.GetForCurrentThread(), Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            _tray.OpenRequested += () => _desktop.Open();
            _tray.ExitRequested += RequestExit;
            _tray.Unavailable += () => { _desktop.Open(); _window.SetDesktopStatus("Tray unavailable. The window stays accessible; use Exit to stop."); };
            _tray.Suspending += OnSuspending;
            _tray.Resuming += OnResuming;
            _tray.ClockChanged += OnClockChanged;
            if (!_tray.EnsureRegistered()) _window.SetDesktopStatus("Tray unavailable. Close will keep this window visible; use Exit to stop.");
        }
        catch (Exception) { _window.SetDesktopStatus("Tray unavailable. Close will keep this window visible; use Exit to stop."); }
        _window.Activate();
        if (OpenProtocolActivation.ClassifyLaunch(Environment.GetCommandLineArgs().Skip(1).ToArray()) == GuiLaunchKind.BrowserWake)
            _window.AppWindow.Hide();
        if (storageFailure is null && _instance is { IsPrimary: true })
        {
            try
            {
                _handoffPipe = new(_services.GetRequiredService<BrowserHandoffService>(),
                    Path.Combine(AppContext.BaseDirectory, "host", "AriaGui.NativeHost.exe"), _lifetime.Token);
                _ = _handoffPipe.StartAsync(_lifetime.Token);
            }
            catch { _window.SetDesktopStatus("Browser handoff is unavailable; browser downloads continue normally."); }
        }
        _updates = RunDownloadsAsync(_services.GetRequiredService<DownloadCoordinator>(), _services.GetRequiredService<ExternalDownloadIntakeService>(), _lifetime.Token);
        _reservationCleanup = RunReservationCleanupAsync(_services.GetRequiredService<DownloadCoordinator>(),
            (IPreparedTakeoverRepository)_services.GetRequiredService<IDownloadRepository>(), _lifetime.Token);
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

    private static bool IsSupportedActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments activation)
    {
        if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol)
            return activation.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol &&
                OpenProtocolActivation.IsOpen(protocol.Uri.OriginalString);
        if (activation.Kind != Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch) return false;
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        return OpenProtocolActivation.ClassifyLaunch(arguments) != GuiLaunchKind.Rejected;
    }
    private static void WriteSyntheticActivationReceipt(Microsoft.Windows.AppLifecycle.AppActivationArguments activation, bool supported)
    {
#if DEBUG || ARIAGUI_VERIFICATION
        // Test-only reason codes in an explicitly marked copied payload. No URI/argument data.
        var marker = Path.Combine(AppContext.BaseDirectory, "r1-fixture-profile.txt");
        if (!File.Exists(marker)) return;
        try
        {
            var profile = Path.GetFullPath(File.ReadAllText(marker).Trim());
            if (!File.Exists(Path.Combine(profile, "synthetic-r1-fixture"))) return;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "activation-fixture.receipt.json"),
                System.Text.Json.JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, Kind = activation.Kind.ToString(), Supported = supported }));
        }
        catch (IOException) { } // Receipt capability is asserted by the fixture, never admission.
        catch (UnauthorizedAccessException) { }
#endif
    }
    private void OnProtocolActivated(object? sender, Microsoft.Windows.AppLifecycle.AppActivationArguments activation)
    {
        if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol && IsSupportedActivation(activation))
            _openActivation?.RequestOpen();
        else if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch
            && activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            && string.IsNullOrWhiteSpace(launch.Arguments)) _openActivation?.RequestOpen();
    }

    private async Task RedirectToPrimaryAsync(Microsoft.Windows.AppLifecycle.AppInstance primary,
        Microsoft.Windows.AppLifecycle.AppActivationArguments activation)
    {
        try { await primary.RedirectActivationToAsync(activation); }
        catch { Win32OpenActivation.RequestExistingWindow(AppDataPaths.RuntimeDirectory); }
        Exit();
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

    private static async Task RunReservationCleanupAsync(DownloadCoordinator coordinator, IPreparedTakeoverRepository repository, CancellationToken lifetime)
    {
        try
        {
            await coordinator.InitializeAsync(lifetime);
            await new PreparedTakeoverMaintenance(repository, TimeProvider.System).RunAsync(lifetime,
                () => coordinator.ReportFailureAsync("Prepared reservation cleanup is pending; the private history store will be retried."));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (DownloadStorageException) { /* Existing startup history failure is reported by RunDownloadsAsync. */ }
        catch (Exception) { await coordinator.ReportFailureAsync("Prepared reservation maintenance is unavailable; keep the private history store for diagnosis."); }
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
            await ((IPreparedTakeoverRepository)_services.GetRequiredService<IDownloadRepository>()).CleanupPreparedTakeoversAsync(_lifetime.Token);
            await _services.GetRequiredService<EngineLifecycleService>().OnResumeAsync(_lifetime.Token);
            await _services.GetRequiredService<DownloadCoordinator>().RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_closing) await _services.GetRequiredService<DownloadCoordinator>().ReportFailureAsync("Resume reconnection is pending; the engine monitor will retry safely.");
        }
    }
    private async void OnClockChanged()
    {
        if (_closing || _services is null) return;
        try { await _services.GetRequiredService<DownloadCoordinator>().NotifyClockChangedAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The periodic engine refresh will retry current wall-time evaluation. */ }
    }

    // Explicit Exit keeps the window alive until commands unwind and both stores checkpoint.
    private async Task ExitOwnedAsync()
    {
        if (_closing) return;
        _closing = true;
        _openActivation?.Stop();
        _browserBridge?.StopAdmission();
        _window?.DisableInput();
        try
        {
#if DEBUG || ARIAGUI_VERIFICATION
        // Bounded owned-fixture interleave: admission is stopped, ownership remains held.
        if (AppDataPaths.VerificationDirectory is { } fixture)
        {
            var hold = Path.Combine(fixture, "phase-c-hold-exit");
            if (File.Exists(hold))
            {
                File.WriteAllText(Path.Combine(fixture, "phase-c-exit-started"), "closing");
                for (var i = 0; i < 50 && File.Exists(hold); i++) await Task.Delay(100);
            }
        }
#endif
            var engineViewModel = _services?.GetRequiredService<EngineViewModel>();
            engineViewModel?.CancelOperations();
            var downloadViewModel = _services?.GetRequiredService<DownloadsViewModel>();
            downloadViewModel?.CancelOperations();
            _services?.GetRequiredService<PreferencesViewModel>().SaveCommand.Cancel();
            _services?.GetRequiredService<PreferencesViewModel>().CheckBrowserCommand.Cancel();
            _lifetime.Cancel();
            if (_handoffPipe is not null) await _handoffPipe.DisposeAsync();
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
            if (_reservationCleanup is not null) await _reservationCleanup;
            if (_engineLoop is not null) await _engineLoop;
            if (_preferencesLoad is not null) await _preferencesLoad;
            if (_services is not null)
            {
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _services.GetRequiredService<ExternalDownloadIntakeService>().DrainAsync(drain.Token); }
                catch (OperationCanceledException) { /* Durable claims are reconciled on restart; never replay. */ }
            }
            // A retry or manual health check may still be unwinding its cancellation.
            foreach (var operation in new[] { engineViewModel?.StartCommand.ExecutionTask, engineViewModel?.CheckCommand.ExecutionTask, engineViewModel?.StopLegacyCommand.ExecutionTask,
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
            _activationInstance.Activated -= OnProtocolActivated;
            _openReceiver?.Dispose();
            _tray?.Dispose();
            _instance?.Dispose();
            _lifetime.Dispose();
            _allowClose = true;
            _window?.Close();
            Exit();
        }
    }
}
