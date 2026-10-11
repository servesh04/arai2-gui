# AriaGUI implementation plan

Status: Phases 0–6 coded; Phase 7 packaging and release tooling are implemented locally. The updated 147-test suite passes on 9 October 2026, including desktop selection/batch behavior, layout preferences, paged sorting, schema upgrade/backup failure tests, the 100-live/10,000-history profile, and recovery regressions. A self-contained Windows 11 x64 MSIX candidate, development signing, dependency notices/inventory, diagnostics export, and a prepared Windows CI workflow are available. See `docs/PHASE_7.md` and `docs/RELEASE_ACCEPTANCE.md`. Phase 7 remains open for clean-machine installation/upgrade/uninstall, production signing, and corresponding-source/redistribution clearance. See `docs/DESKTOP_UI_TEST.md` for native desktop refresh results; broader accessibility, tray/sleep/disk-full acceptance, and debugger checks remain pending. Optional Phase 8 work follows MVP acceptance.

Prepared on 2 October 2026. The project folder was empty when this plan was prepared. The starting architecture is `ARCHITECTURE (2).md`, supplied by the user. This plan incorporates the five design changes accepted during its review.

**Desktop design update — 8 October 2026:** the latest reference calls for a compact native transfer table, adjustable panes, multi-selection, batch commands and persisted layout. See [Desktop UI refresh](docs/DESKTOP_UI_PLAN.md) for the reviewed references, current capability mapping and implementation increments. This is an update to the presentation direction using the existing WinUI 3/backend foundation. Browser handoff remains a separate feature track; Phase 7 release acceptance remains open.

**Responsive layout follow-up — 9 October 2026 (R0 partially complete, not coded):** the supplied maximized-window screenshot reports table gaps/header misalignment and inflexible filtering. [Responsive UI review and implementation plan](docs/RESPONSIVE_UI_PLAN.md) adds R0 reproduction, R1 shared responsive table geometry, R2 adaptive shell/filter components, R3 responsive panes/dialogs and preference compatibility, and R4 resize/DPI regression verification. R0 reproduced the maximized fixed-width gap and capped search; the large header mismatch, settings provenance, controlled DPI/client geometry, recycling and compact breakpoints remain unresolved. R1 may start on established defects after its unchanged-layout instrumentation baseline, while those acceptance requirements stay open. The 147-test baseline and earlier fixed-size native checks do not establish resize correctness.

## 1. What we are building

A native Windows download manager using C#, WinUI 3, and aria2. The first release supports HTTP/HTTPS URL downloads, queue control, progress, history, and recovery after interruption. aria2 does the downloading; our code manages the user experience and coordinates the engine.

We will develop in small, runnable steps. A phase is finished when its demonstration and checks pass, not merely when its classes exist. Tests accompany behavior as we introduce it.

Initial development target: Windows 11 x64. Start with the packaged WinUI app template and an MSIX distribution path. Pin compatible stable .NET, Windows App SDK, EF Core, and aria2 versions during setup after checking the machine. Broader Windows compatibility can be tested later; it is not an initial promise.

## 2. Accepted design decisions

| Decision | How we will implement it | First phase |
| --- | --- | --- |
| Durable commands and recovery | Save command intent before engine mutations; confirm outcomes and reconcile uncertain operations. Preserve engine session/control files separately from SQLite. | 3 |
| Explicit ownership of state and queue execution | aria2 owns execution. The application owns desired settings and priorities. One coordinator applies commands and observations to shared runtime state. | 1, 4 |
| Separate jobs, transfers, and files | Stable `DownloadJob` identity, `TransferAttempt` records for engine transfers/retries, and `DownloadFile` output records. Live progress is a separate snapshot. | 1, 3 |
| Bounded synchronization | One non-overlapping polling loop, lightweight active snapshots, paged history, and coalesced UI updates. WebSocket notifications are optional later. | 3, 6 |
| Secure, predictable process ownership | One app instance, one owned engine, localhost RPC with a generated secret, controlled recovery, and an explicit Close/Exit policy. | 2, 5 |

Final MVP lifecycle: Close hides the window to the tray; Open restores it; Exit saves state and stops the owned engine. During early phases, before the tray exists, Close performs a clean Exit. We will label that temporary behavior in the walkthrough. No Windows service or automatic startup is required for the MVP.

## 3. How the parts fit together

| Part | Plain-language meaning | What we put there |
| --- | --- | --- |
| `AriaGui.App` | The screen and its connection to application behavior | XAML pages, ViewModels, UI dispatch, tray integration, startup wiring |
| `AriaGui.Core` | The vocabulary and rules of downloading | Jobs, files, attempts, states, valid transitions |
| `AriaGui.Application` | The actions the product can perform | Add/pause/resume workflows, command coordination, state reconciliation, interfaces for external dependencies |
| `AriaGui.Infrastructure` | Code that talks to things outside the application | aria2 process/RPC, SQLite/EF Core, configuration and logging adapters |
| Test projects | Repeatable evidence that behavior works | Domain tests, fake-engine workflow tests, and real-engine integration tests |

XAML describes the interface. A ViewModel exposes the data and commands that interface displays. A binding connects a control to a ViewModel property, so changing progress can update the screen without manually repainting it. This separation is called MVVM.

Application code calls an interface such as `IAria2Client`. Infrastructure supplies the real implementation; tests supply a fake. Dependency injection connects these objects at startup. It does not require a separate server.

Project references: Application references Core; Infrastructure references Application and Core; App references Application and Infrastructure to assemble the application. Core references none of the other projects. Views and ViewModels use application services even though the App project also wires up Infrastructure. Engine and persistence interfaces live in Application so Core stays independent of aria2.

Intended folder layout, created as needed:

```text
src/
  AriaGui.App/
  AriaGui.Core/
  AriaGui.Application/
  AriaGui.Infrastructure/
tests/
  AriaGui.Core.Tests/
  AriaGui.Application.Tests/
  AriaGui.Infrastructure.Tests/
docs/
  DEVELOPMENT.md
  WALKTHROUGH.md
```

We will use xUnit for tests and CommunityToolkit.Mvvm for repetitive ViewModel property/command plumbing. Exact package versions are setup decisions, recorded with the project. Avoid adding abstractions before there is a real testing or implementation need.

## 4. Phase roadmap

| Phase | Working milestone | Main concept you learn |
| --- | --- | --- |
| 0 | A blank Windows app launches under the debugger | Toolchain, solution, build, run |
| 1 | A simulated download appears and updates | XAML, MVVM, domain models, dependency injection |
| 2 | The app securely starts and stops aria2 | Child processes, RPC, asynchronous operations |
| 3 | A real URL downloads and completed history survives restart | End-to-end workflow, SQLite, durable commands |
| 4 | Pause/resume, queue order, removal, and retry work | Requested versus observed state, command ordering |
| 5 | Downloads recover after interruption; Close/Exit work predictably | Reconciliation, failure testing, app lifecycle |
| 6 | A large queue remains usable and errors are understandable | UI threading, virtualization, bounded data flow |
| 7 | A tested installer works on a clean Windows machine | Packaging, runtime dependencies, upgrades |
| 8 | Optional enhancements selected after the MVP | Extending a proven foundation |

Phases 0–3 produce the first useful preview. Phases 4–7 complete the dependable MVP. Phase 8 is a separate backlog.

## 5. Phase 0 — Get a Windows app running

**What you will see:** an AriaGUI window launched from Visual Studio, with a small text change we can rebuild and see.

**Coding steps:** inspect installed tools; configure the WinUI development workload and SDKs if needed; create the C# WinUI application and solution; select x64; record version pins and repeatable build/run instructions. Initialize source control if it is not already present and add appropriate generated-file exclusions.

Use Visual Studio as the beginner-friendly debugging environment. Microsoft's current [WinUI quickstart](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here) describes its setup and the packaged C# template. Check the installed toolchain before choosing exact versions or installing anything.

**What we will explain:** a solution groups projects; a project compiles source and declares dependencies; NuGet supplies libraries; a debugger lets us stop inside running code. We will show where application startup and the first window live.

**Done when:** build and launch succeed, a breakpoint is hit, and `docs/DEVELOPMENT.md` records how to repeat those steps. Include an early local package/deployment smoke check so packaging problems surface before release week. No feature tests are needed for a blank window.

## 6. Phase 1 — Build the screen with a fake engine

**What you will see:** a download list with simulated progress and a fake Add action.

**Coding steps:** add the Core, Application, and Infrastructure projects; wire dependency injection; create `DownloadsPage`, `DownloadsViewModel`, and `DownloadItemViewModel`; implement a fake `IAria2Client` for demonstrations and tests. Introduce a shared runtime store owned by the application coordinator.

Define these concepts before database entities:

| Model | Meaning |
| --- | --- |
| `DownloadJob` | The user's task, identified by a stable application ID |
| `TransferAttempt` | An engine transfer associated with a job; stores its GID and outcome |
| `DownloadFile` | An output file belonging to the job/transfer |
| `DownloadSnapshot` | Latest observed progress, speed, status, and nullable size/ETA |
| `PendingOperation` | A requested action whose execution may still need confirmation |

The initial job has one transfer and one file. The model can later associate multiple transfers/files and parent-child transfer relationships without adding advanced torrent screens now. Requested action, engine-observed status, and engine connectivity are separate values.

**What we will explain:** trace a button through its ViewModel command into an application service. Compare a model, a ViewModel, and a future database entity. Show why unknown file size means an indeterminate progress indicator.

**Done when:** multiple ViewModels observe the same runtime state; fake progress updates on the UI thread; domain tests cover valid transitions and unknown totals. Screens make no engine or database calls directly.

## 7. Phase 2 — Own the aria2 process securely

Implemented on 3 October 2026: pinned aria2 1.37.0, typed local RPC, ownership/instance leases, protected runtime configuration, bounded diagnostics, readiness/failure UI, and shutdown. See `docs/PHASE_2.md` for the code trace and verification evidence. Early Close performs Exit; automatic adoption and tray behavior remain Phase 5 work.

**What you will see:** Starting engine, Ready, or a useful startup failure message.

**Coding steps:** bundle a pinned aria2 binary; implement `Aria2ProcessManager`, a typed RPC client, and cancellation/timeouts. Start the executable through structured arguments, capture diagnostic output, and verify readiness with an authenticated RPC call. Add single-instance coordination before engine startup.

Use writable per-user application storage for configuration, sessions, database, and logs. Installed binaries and mutable files must have different locations. Generate an RPC secret, protect its stored configuration for the user, bind RPC to loopback, and redact secrets and sensitive URL components from logs. aria2 exposes [RPC token authentication](https://aria2.github.io/manual/en/html/aria2c.html#rpc-authorization-secret-token).

Record ownership and connection metadata. A port conflict or an unrecognized process produces a clear failure; it must not cause the app to connect to or terminate an arbitrary engine. Safe adoption of a surviving owned engine will be completed in Phase 5.

**What we will explain:** aria2 is a separate program, and JSON-RPC is how we ask it to do work. A started process may not yet be ready. `async`/`await` lets a caller wait for a result without treating the UI as a blocking control loop.

**Done when:** real-engine integration checks cover startup, authentication failure, missing binary, occupied port, startup timeout, and clean shutdown. Launching the app twice does not start two engines. No real downloads are required yet.

## 8. Phase 3 — Complete the first durable real download

Implemented on 4 October 2026. See `docs/PHASE_3.md` for the transaction/RPC trace, native demonstration, and automated verification. Completed history survives a fresh engine; interrupted jobs are explicitly marked for recovery, without an automatic resubmission. Recovery remains Phase 5 work.

**What you will see:** paste a URL, choose a destination, download a file, see completion, restart the app, and find the completed record in history.

**Coding steps:** introduce SQLite/EF Core, initial migrations, repositories, `AddDownloadAsync`, and a single synchronization loop. Persist jobs, attempts, files, pending operations, and meaningful terminal outcomes. Keep frequently changing progress in memory with controlled checkpoints.

The Add workflow is deliberately explicit:

1. Validate the request and destination/collision policy.
2. Commit the job, attempt, and pending Add operation to SQLite.
3. Send the engine request using a preallocated valid GID for this attempt.
4. Confirm the outcome and update the durable record.
5. Observe engine progress and publish a lightweight snapshot to the UI.

aria2 permits [caller-specified GIDs](https://aria2.github.io/manual/en/html/aria2c.html#cmdoption-gid). Keep the application ID separate. An RPC request ID only matches a response; it does not prevent duplicate downloads. On timeout, leave the operation uncertain and check the known GID before any retry. If its outcome cannot be established, surface recovery status instead of blindly submitting again. There is no transaction spanning SQLite and aria2.

Enable periodic engine session persistence and preserve control files. These contain different recovery information from application history. Test uncertain Add outcomes now; expand the restart matrix in Phase 5.

Use short-lived database contexts created for each unit of work, with controlled background database execution. [EF Core contexts are not thread-safe](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/). Also, [Microsoft.Data.Sqlite async methods execute synchronously](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async), so adding `Async` to calls does not by itself protect UI responsiveness. Establish one deliberate background execution boundary, rather than scattering `Task.Run` calls throughout the app.

**What we will explain:** SQL tables versus C# objects, migrations as versioned schema changes, a database transaction, and why “the request timed out” does not mean “the engine rejected it.” Walk through one Add request with breakpoints.

**Done when:** a controlled local HTTP test server supplies a known file; downloaded contents match; completion survives restart; an injected lost Add response does not create a duplicate; denied destination access produces a useful error. A real internet URL is an additional manual smoke check, not the only test fixture.

## 9. Phase 4 — Implement commands and queue policy

Implemented on 4 October 2026; see `docs/PHASE_4.md` for the beginner lesson, command semantics, and 96-test verification record. Native button/layout inspection is pending after Computer Use was stopped. The new database migration preserves Phase 3 history. Remove retains files/history; Retry links a new attempt; waiting reordering does not preempt active jobs. Notifications currently use an in-app completion notice, deduplicated by the persisted attempt transition.

**What you will see:** working Pause, Resume, Remove, Retry, queue reordering, and a concurrent-download limit.

**Coding steps:** send commands through the coordinator and durable operation mechanism. Serialize conflicting commands for the same job; apply observations in a defined order and reject stale observations after engine reconnection. Keep routine network waits outside the critical state-update section.

aria2 owns the running/waiting queue. Application priorities translate to engine operations, including [queue position changes](https://aria2.github.io/manual/en/html/aria2c.html#aria2.changePosition). Do not run a second scheduler that independently starts and pauses jobs to imitate the same queue.

Show Pausing or Resuming while the outcome is pending, then confirm through observation. Distinguish connection loss from an actual download failure. Document how priority applies to waiting downloads and whether active jobs are preempted; the MVP will not preempt active jobs solely because another job gains priority.

Define Remove as stopping/removing the engine transfer while retaining history and files. File deletion is a separate explicit action if later implemented. Resume continues the same transfer; Retry creates a new attempt under the same job after reconciling the old attempt; Redownload deliberately starts fresh. Coordinate retry limits with engine retries to avoid two retry loops fighting each other.

**What we will explain:** the screen can acknowledge a click before the engine has finished applying it. Demonstrate a pause request that races with completion and explain the resulting state.

**Done when:** tests cover double clicks, rapid pause/resume, queue reorder, removal during progress updates, retry after failure, and command timeouts. A stale progress update cannot undo a confirmed terminal state. Completed notifications are deduplicated by durable transition identity.

## 10. Phase 5 — Finish recovery and desktop lifecycle

Implemented on 6 October 2026. The code walkthrough and automated/manual failure matrix are in `docs/PHASE_5.md`. Remaining manual lifecycle cases are tracked there rather than treated as completed by a build alone.

**What you will see:** useful recovery status after interruption, downloads restored where possible, a tray icon, and predictable Close/Open/Exit behavior.

**Coding steps:** reconcile database jobs, pending commands, engine inventory/session state, and files. Maintain an engine-generation identifier so observations from an earlier connection cannot overwrite current state. Never infer completion solely from a missing GID or a file existing on disk.

On startup, first determine whether the previously owned engine survived. Reconnect only when recorded ownership and authenticated endpoint checks agree; otherwise restore the session into a new owned engine. Unknown engine entries are reported for reconciliation and are not silently deleted. Preserve paused intent and avoid resubmitting active transfers blindly.

Add bounded restart attempts with backoff, suspend/resume handling, and a clear manual recovery option after repeated failure. Add the tray adapter; Close now hides the window only after the tray is available, while Exit checkpoints state, requests engine shutdown, waits with a timeout, and handles any remaining owned process explicitly. Avoid pipe capture arrangements that unintentionally prevent a surviving engine from operating after a GUI crash.

**What we will explain:** reconciliation means comparing evidence and deciding what is actually true. App history is not proof that an engine is currently downloading; engine session data is not a substitute for product history.

**Done when:** an automated/manual failure matrix covers crashes before RPC submission, after engine acceptance, and before outcome persistence; engine crash; lost connection; missing session/control files; disk-full errors; Windows sleep/resume; and an unrelated process occupying the previous port. Verify no duplicate downloads, no silent file deletion, and a truthful recovery/error state when continuation is impossible. Force termination only in isolated test environments.

## 11. Phase 6 — Make the application comfortable at scale

Implemented on 6 October 2026. See `docs/PHASE_6.md` for the code trace, declared workload budgets, measured SQLite/ViewModel results, and remaining native/manual checks.

**What you will see:** a responsive list, useful error messages, search/filtering, paged history, basic settings, and accessible keyboard navigation.

**Coding steps:** tune one polling loop with selected response fields, frequent active progress, slower waiting/stopped reconciliation, and idle backoff. Bound pending work and coalesce superseded progress updates without dropping commands or terminal transitions. Persist completion promptly rather than relying on unlimited engine history.

Virtualize the list so it creates only the visible row controls. Dispatch UI changes in batches. Keep graph samples bounded if charts are added; charts can also wait until Phase 8. Page database history and expose Open file/Open folder only when appropriate. Add light/dark theme support, accessible names, pending-action indicators, and useful empty/error states.

**What we will explain:** reducing the amount of work is often more effective than adding threads. Show how a large history differs from a small set of live transfers.

**Done when:** profile a proposed test workload of 100 active simulated transfers and 10,000 historical records on a recorded reference machine. Confirm no overlapping polls, bounded memory/work queues, and responsive typing/scrolling. Use a modest real-download workload separately. Set measured latency/memory budgets before optimizing and record results; these workloads are validation targets, not throughput claims.

WebSocket events remain optional. They can trigger refreshes, but polling and reconnect reconciliation still provide progress and recovery.

## 12. Phase 7 — Package and release the MVP

Implemented locally on 6 October 2026: self-contained MSIX build/content/signature validation, file-only development signing, dependency notices/inventory, verified pre-migration SQLite backups and newer-schema rejection, diagnostics export, release notes/acceptance matrix, and a Windows GitHub Actions workflow prepared for future hosting. No package/certificate was installed, no VM was started, and no public release was published. Clean-machine and distribution gates remain pending.

**What you will see:** an installable package that works on a Windows machine without developer tools or a manually installed aria2.

**Coding steps:** finalize the MSIX path; include aria2 and the selected .NET/Windows App SDK runtime deployment strategy; verify install locations and writable data paths. Document development certificate trust versus production signing. Record engine provenance, version, checksum, and bundled notices; verify distribution requirements before release.

Add a Windows build/test pipeline when repository hosting is available. Exercise versioned schema migrations with a backed-up older database, and provide a defined recovery path if migration fails. Document what upgrade and uninstall do to app data, settings, partial downloads, and completed files. Capture diagnostics without exposing credentials.

**What we will explain:** a build produces binaries, while packaging delivers the files and dependencies another machine needs. A signing certificate establishes publisher trust; a schema migration upgrades saved data.

**Done when:** install, launch, real download, pause/resume, restart recovery, upgrade, and uninstall have been tested in a clean Windows VM or separate test machine. The package runs without development SDKs. Release notes list supported OS/architecture and remaining limitations. Passing a developer-machine build alone does not satisfy this phase.

MVP acceptance: reliable Add → Download → Pause/Resume → Complete → History → Restart/Recover, responsive UI, protected local RPC, predictable process ownership, and successful clean-machine installation.

## 13. Phase 8 — Optional product enhancements

Choose one enhancement at a time after Phase 7. Suggested order: richer details and speed charts; tags and profiles; scheduling/bandwidth rules; torrent/Metalink UI with multi-file selection; browser integration. AI automation remains a later application-service client and does not bypass the same command/recovery rules.

Each enhancement gets its own small design and acceptance criteria. None blocks the first release.

## 14. How we will code together

For each small implementation step, we will:

1. State the user-visible behavior we are adding and the concept it teaches.
2. Identify the handful of files involved and explain each file's responsibility.
3. Implement the smallest working change and add meaningful checks for its behavior.
4. Run it, demonstrate the result, and explain any failure before proceeding.
5. Record what changed, what passed, and the next step in `docs/WALKTHROUGH.md`.

The walkthrough will use examples from our actual code, including tracing Add and Pause through the layers. Explanations happen alongside implementation. We will keep working within the requested phase without requiring approval for every routine file edit.

The first coding task is Phase 0 only: inspect the toolchain, scaffold the WinUI app, run it, and document how startup reaches the first window. This keeps the first lesson concrete and establishes a verified foundation for the following phases.
