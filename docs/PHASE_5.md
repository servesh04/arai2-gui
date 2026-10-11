# Phase 5: recovery and Windows app lifetime

Implemented on 6 October 2026. This phase connects the durable intent from Phase 4 to engine recovery and changes the desktop lifecycle: **Close hides; Open restores; Exit saves and stops**. Native verification and the final automated results are recorded below.

## Try the behavior

Run `scripts/Run.ps1 -Unpackaged`. Close the window with its X button: downloads keep running and the tray icon provides **Open AriaGUI** and **Exit**. Starting another copy asks the existing window to open. The window also has an explicit **Exit** button. If the tray cannot register, Close keeps the window accessible and displays a message.

Pause a download, Exit, and restart. Its attempt should return paused, with the same engine transfer ID. An active unfinished transfer can continue from a validated session/control file. Rows whose evidence is missing show **Needs recovery**. **Recover** checks the original ID first and applies the saved intent; **Retry** remains the Phase 4 action that creates a new attempt for a failed or removed transfer.

Do not force-close your everyday app to try crash recovery. The automated fixtures use separate databases, folders, engine ports, and host processes.

## Lesson 1: four different kinds of evidence

| Evidence | What it tells us |
| --- | --- |
| SQLite job, attempt, operations | Which download belongs to us and what the user requested |
| Authenticated engine inventory | Which transfers exist and their current status |
| Saved engine session | Which unfinished transfer IDs/URIs were checkpointed |
| Download and `.aria2` control file | Output bytes and aria2's continuation information |

Reconciliation compares these sources. A database row alone does not prove a transfer is running. An existing output file alone does not prove it is complete. We preserve the application job and attempt identities during recovery; observing an authenticated completed GID supplies completion evidence.

`DownloadEntry.DesiredPaused` and `DesiredRemoved` store intent separately from the last observed status. `InsertOperationAsync` commits these flags with the command before RPC. For example, a GUI crash after saving Pause but before sending it must still leave a request to pause. The `RecoveryIntent` migration seeds existing Phase 4 rows using the last valid operation and observed status; a later Resume overrides an earlier Pause.

## Lesson 2: reconnect or restore

Follow `Aria2ProcessManager.StartAsync` in Infrastructure:

1. Acquire the protected runtime lease and verify the bundled executable's hash.
2. Load saved history and look for a surviving recorded engine.
3. Before adopting it, verify PID **and start time**, actual executable path, previous owner's absence, private configuration, loopback port, secret authentication, engine version, and session ID.
4. After proof succeeds, keep the same process, reconcile known intents, and write new ownership metadata.
5. If that process is gone, check that the port is free and prepare a fresh engine session.

Failure to prove ownership keeps the existing process and metadata intact and reports a failure. We never use a process name or port alone as permission to adopt or stop an engine.

`SessionRecoveryPlanner` reads a bounded checkpoint/session file, matches GIDs and URIs against SQLite, and validates destinations. It builds a **new restricted input file** from known nonterminal jobs; saved option lines are not executed. Completed, failed, removed, and desired-removed jobs are excluded. Unknown session records stay in the original file and are reported. Unknown transfers in a surviving engine are counted without becoming application jobs or receiving commands.

Every restored transfer initially starts paused. The manager saves this checkpoint, then resumes entries whose saved intent requests execution. This protects paused jobs and gives us a durable checkpoint before network activity resumes. Missing sessions, invalid metadata, and existing output without a control file produce a review/recovery state. Recovery does not delete output or automatically rename/overwrite it.

`engine.checkpoint.json` points to the most recent saved session, independently of the current process ownership record. Clean Exit removes the ephemeral configuration and owner record, while retaining session/control files and SQLite. Legacy Phase 4 session files can be found when no pointer exists.

## Lesson 3: why launching the engine changed

The engine can now outlive a GUI crash. We removed `stop-with-process` and redirected standard-output/error pipes. `NonInheritingProcessLauncher` uses `CreateProcessW` with **handle inheritance disabled**, an explicit executable, a quoted argument list, and no console window.

This matters on Windows: inheriting the GUI's instance/engine lock handles would keep those locks open inside aria2 after the GUI dies. A restarted GUI could then be unable to acquire its own locks. Explicit Exit still saves and stops only the process that this manager started or fully verified and adopted. The lease has a bounded sharing-violation retry to tolerate final handle cleanup immediately after process termination; it never bypasses a held lock.

The engine writes session/control checkpoints every five seconds. A crash between checkpoints can lose recent recovery metadata; we do not promise zero lost progress or complete recovery from every interruption.

## Lesson 4: automatic engine recovery and explicit row recovery

`EngineLifecycleService.RunAsync` owns one health monitor. Transient process-exit, RPC-unavailable, and startup-timeout failures get at most **three automatic retries**, with delays of **2, 4, and 8 seconds**. Poll cadence/startup duration can make actual wall-clock delays longer. Thirty seconds of healthy operation resets the budget. **Start engine** resets it explicitly. Ownership, authentication, binary, and occupied-port failures require correcting the cause.

`DownloadCoordinator` keeps its observation generation/sequence guards. A newly restored engine generation can restart an interrupted active attempt; observations from an old generation cannot overwrite current state. Terminal records remain terminal.

For an uncertain or interrupted row, trace the Recover button:

```text
DownloadsPage → DownloadItemViewModel.RecoverCommand
  → DownloadsViewModel → DownloadCoordinator.RecoverAsync
  → SQLite: atomically supersede the old uncertain operation and save Recover
  → ManagedAria2Client → Aria2ProcessManager.RecoverDownloadAsync
  → authenticate and check the original GID
  → apply saved pause/remove/queue intent to an existing transfer
     OR validate continuation and explicitly add the absent original GID
  → SQLite confirmation → runtime store → UI bindings
```

An Add intent absent from both engine and validated session is **not automatically resubmitted**. Explicit Recover makes that action deliberate after verifying absence. A saved Remove whose GID is absent becomes Removed without starting it or deleting its files. Recover does not create a second attempt; Retry does.

## Lesson 5: the tray is a Windows adapter

`DesktopLifetimeController` contains the small, testable Close/Open/Exit policy. `App.xaml.cs` supplies the host actions and cancels/awaits commands and loops before saving history and stopping the engine. Duplicate Exit requests share one task.

`Win32TrayIcon` supplies the Windows behavior through the existing WinUI HWND. It registers the notification icon, installs a window-message subclass, provides the native Open/Exit menu, handles Explorer recreating the taskbar, and listens for the per-user second-instance Open message. It does not introduce a WinForms app or a second UI loop.

Windows suspend messages request best-effort database and engine checkpoints with a two-second budget. Resume checks the connection and refreshes history through the existing gates. This is not a guarantee that Windows waits for asynchronous checkpoint completion before suspending. Regular checkpoints remain necessary.

## Verification — 6 October 2026

`scripts/Verify.ps1` passed: locked dependency restore, bundled engine hash check, full x64 solution build with **zero warnings/errors**, and **125 tests** (16 Core, 47 Application, 62 Infrastructure; none skipped). Both the original behaviors and the new isolated recovery cases are included. The unpackaged app also built cleanly and launched; native accessibility inspection showed Ready, aria2 1.37.0, restored completed history, Recover controls, and the explicit Exit button. This inspection did not exercise the tray menu or window-close actions. Do not treat the outstanding manual cases as passed.

| Scenario | Evidence |
| --- | --- |
| GUI crash, engine survives | Isolated real-engine test: same PID/GID; paused intent kept; no second Add |
| Engine and GUI crash | Isolated real-engine test: restored original GID; resume produces exact fixture bytes |
| Crash before Add RPC | Isolated host stops after durable intent; no automatic HTTP request; explicit Recover completes the same attempt |
| Crash after engine acceptance, before confirmation | Isolated host stops before coordinator persistence; original GID reconciles; one HTTP transfer |
| Pause/Remove not submitted | Real-engine tests exercise saved intent and file preservation |
| Failed ownership proof | Wrong secret, session, or executable leaves survivor and metadata intact |
| Missing session/control file | Recovery is surfaced; existing bytes preserved; unsafe overwrite rejected |
| Unknown session/inventory entries | Original session preserved; unknowns reported without owned-job mutations |
| Engine failure while GUI is alive | New generation restores the same active attempt and exact bytes |
| Clean Exit and restart | Paused session/history restored using an independent checkpoint pointer |
| Phase 3/4 database upgrade | Old history preserved; latest Pause/Resume/Remove intent carried forward |
| Bounded restart, tray fallback, duplicate Exit | Application tests with controlled clock/host |
| Occupied port, corrupt DB, inaccessible storage, timeout | Existing process/storage tests retained |
| Native Close/Open/tray menu/Exit | Pending native smoke inspection |
| Native launch/readiness/new controls | Passed unpackaged build/launch and accessibility inspection; tray interactions pending |
| Actual Windows sleep/resume and Explorer restart | Pending manual Windows verification; lifecycle callbacks are tested separately |
| Actual disk-full volume | Pending disposable-volume/VM test; no disk was filled on this machine |
| Packaged launch and debugger exercises | Still pending; unpackaged launch supported |

Crash tests forcibly terminate only their own isolated host/verified engine processes and clean their own GUID temporary directories. They use an HTTP server that supports byte ranges and compare downloaded content. Automated policy tests do not replace inspecting the tray and native window.

Read next: Phase 6 focuses on scale, UI responsiveness, history paging, accessibility, and clearer errors. The manual lifecycle/failure checks above remain part of Phase 5 acceptance.

## References

The engine behavior uses aria2's [RPC methods and session support](https://aria2.github.io/manual/en/html/aria2c.html#rpc-interface). The Windows adapter uses Microsoft's [CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw), [notification icon data](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw), and [window subclass API](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass).
