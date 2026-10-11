# Phases 0 and 1: from a Windows window to observable downloads

This page records the original simulated preview. Continue with [Phase 2: processes and RPC](PHASE_2.md), [Phase 3: real downloads and SQLite](PHASE_3.md), [Phase 4: commands and queue policy](PHASE_4.md), [Phase 5: recovery and Windows lifetime](PHASE_5.md), [Phase 6: responsive downloads and browsable history](PHASE_6.md), and [Phase 7: packaging and upgrades](PHASE_7.md). The current app uses real downloads and Close-to-tray; the simulator and early Close policy described below are historical learning steps.

## What is working

There is a native WinUI preview with two example downloads, an Add form, progress bars, speed and ETA labels, unknown-size handling, and a shared summary footer. It follows the system theme. The fake engine uses elapsed time instead of downloading anything. Each launch starts fresh.

The implementation uses four production projects. `Core` describes downloads; `Application` coordinates actions and observed state; `Infrastructure` supplies the fake engine; `App` presents everything on screen.

## Lesson 1: application startup

Start with `src/AriaGui.App/App.xaml.cs`.

WinUI calls `OnLaunched`. It registers services in a `ServiceCollection`, builds the dependency-injection container, creates a page and window, and activates that window. The registration of `IAria2Client` to `FakeAria2Client` is the deliberate engine-selection point.

Singleton means one shared instance for the app lifetime. Both ViewModels receive the same `DownloadRuntimeStore`. This is why the footer can agree with the download list without contacting the engine itself.

The window hosts `DownloadsPage`. Its code-behind only receives its two ViewModels and initializes XAML. There are no download rules or engine calls inside the page.

## Lesson 2: the Add button

Open `src/AriaGui.App/Views/DownloadsPage.xaml` and find `AddDownloadCommand`.

The button's command binding points to `DownloadsViewModel.AddDownloadCommand`. The text box binds in both directions: typing updates `InputUrl`, and a future property change could update the text box. A one-way binding, such as the progress value, only displays data from the ViewModel.

`AsyncRelayCommand` runs `AddDownloadAsync` and prevents overlapping executions of that command while it is busy. The ViewModel asks `DownloadCoordinator` to add the URL. Invalid input becomes a readable error message next to the form.

Follow `DownloadCoordinator.AddAsync` in `src/AriaGui.Application/Downloads`. It validates the URL, creates a job, a transfer attempt, a file record, and an in-memory pending operation. It calls the engine through `IAria2Client` and publishes the result to the store. No HTTP request occurs: the injected implementation is the simulator.

The application ID is not the engine ID. A future retry can retain the user's job while creating a different transfer attempt. Pending operations currently disappear with process memory; they are foundations for Phase 3, not a crash-recovery guarantee.

## Lesson 3: how progress reaches the screen

`FakeAria2Client` receives a `TimeProvider`. It calculates how much simulated time has passed and returns a `DownloadSnapshot`. Tests replace the clock so minutes can pass instantly without sleeps.

`DownloadCoordinator.RunAsync` owns one 500 ms `PeriodicTimer`. It refreshes engine observations, respects allowed state transitions, and publishes immutable runtime snapshots. A semaphore serializes Add and Refresh in this small preview. Later engine integration will revisit the duration of that critical section.

`RuntimeViewModel` subscribes to the store and sends changes through `IUiDispatcher`. The Windows implementation uses `DispatcherQueue` to apply bindings on the UI thread. Each snapshot carries a revision so an older queued update cannot overwrite a newer one.

`DownloadsViewModel` updates existing row objects instead of recreating the whole list. `DownloadSummaryViewModel` observes the same snapshot to calculate totals and speed. Neither ViewModel polls aria2. The observable properties notify WinUI, and bindings update the controls.

## Lesson 4: unknown information stays unknown

A `null` total size means the engine does not yet know how large the file is. It is different from a zero-byte file. `DownloadSnapshot` returns no percentage or ETA for unknown totals; the ViewModel selects indeterminate progress and displays “Size unknown” and “Estimating…”.

Try the unknown-size checkbox. The simulator reveals the total at 12 seconds and switches naturally to a normal progress bar. Known zero-byte completed files are also covered by a unit test so there is no divide-by-zero error.

## Lesson 5: closing the app

For this preview, Close exits. `App` cancels its lifetime token, awaits the synchronization loop, and disposes the services/subscriptions. There is no tray icon or child aria2 process yet. Phase 5 will introduce the final Close-to-tray policy.

## Verification record — 2 October 2026

| Check | Result |
| --- | --- |
| Final locked restore and full x64 solution build | Passed through `scripts/Verify.ps1`; zero warnings/errors |
| Domain tests | 16 passed |
| Application, simulation, and ViewModel tests | 15 passed |
| Unpackaged native window launch | Passed |
| Seeded progress and shared summary | Observed in the running Windows UI |
| Invalid URL | Error displayed; download count unchanged |
| Valid Add following an invalid URL | New row added and error cleared |
| Unknown-size Add | Indeterminate progress, “Size unknown”, and “Estimating…” observed |
| Completion | Both seed rows reached 100%; summary showed two completed and zero active |
| Close | Process exited with code 0 |
| Packaged launch | Attempted; Windows blocked it because Developer Mode was off |
| Visual Studio breakpoint exercise | Documented for the learner; not yet manually exercised |

The tests cover terminal-state regression, missed intermediate polling states, distinct identity, input validation, immutable snapshots, shared observers, dispatching, stale updates, disposal, a lost Add response, concurrent requests, and loop cancellation. The ViewModel test project links the actual platform-neutral ViewModel sources so it can test them without launching WinUI. These tests do not replace the native UI smoke check.

UI inspection found two polish issues: an exception parameter name appeared in the validation message, and the initial physical-pixel window size left little list space on a scaled display. The corrected message and larger work-area-bounded window were verified in a second native launch. Both sample cards now fit in the initial window on the development display. Individual filename text and progress bars are accessible; the list container's generic accessibility label remains a Phase 6 polish item.

## Next coding step

Phases 2–6 and local Phase 7 release tooling are implemented. Read [PHASE_7.md](PHASE_7.md) for packaging, signing, backup/recovery, and the latest verification record. Finish the [release acceptance matrix](RELEASE_ACCEPTANCE.md), including clean-machine install/upgrade/uninstall and native usability, before treating the MVP as released. Optional Phase 8 enhancements follow acceptance.

The October desktop redesign is explained in [DESKTOP_UI_WALKTHROUGH.md](DESKTOP_UI_WALKTHROUGH.md). Its compact native workspace, selection/batch behavior, sorting and layout preferences build on those earlier phases. See [DESKTOP_UI_TEST.md](DESKTOP_UI_TEST.md) for verification of the updated application.
