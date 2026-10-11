# Phase 2: owning a real aria2 engine

This page records the Phase 2 milestone. Phase 3 now connects the real download form and SQLite history; see [PHASE_3.md](PHASE_3.md). On 4 October 2026 the previously pending native Check connection, picker/form layout, and clean Close/reopen checks passed. The historical verification table below retains the original 3 October results.

## What you can try

Run `scripts/Run.ps1 -Unpackaged`. The new Download engine panel starts at Starting, then should show Ready and aria2 1.37.0. Click Check connection to repeat the health check. Closing the window performs a clean Exit in this phase. Launching another copy while one is open silently exits the second copy; bringing the first window forward is later lifecycle work.

The Add form and download rows remain the Phase 1 simulation. Ready means we have verified the engine connection; it does not mean those rows represent real transfers. Phase 3 will connect the download workflow to aria2 and add saved history.

## Lesson 1: a process is a running program

The GUI and `aria2c.exe` run as two Windows processes. aria2 handles transfers; the GUI controls it. We bundle a specific engine so a user does not need to install or locate one manually.

Start at `App.OnLaunched` in `src/AriaGui.App/App.xaml.cs`. It acquires `SingleInstanceLease` before creating the window or starting an engine. The lease holds an exclusive file handle; Windows releases that handle if the app dies. It then registers `IManagedAria2Engine` with `Aria2ProcessManager`, builds the window, and starts the asynchronous engine loop.

The download interface, `IAria2Client`, is still registered with `FakeAria2Client`. These two registrations make the phase boundary visible: real process lifecycle, simulated download behavior.

Open `src/AriaGui.Infrastructure/Aria2/Aria2ProcessManager.cs` and follow `StartAsync`:

1. Acquire an exclusive engine lease; refuse a recorded process that is still alive.
2. Verify the bundled binary hash and check that the loopback port is available.
3. Generate a random 256-bit secret and write a private configuration file.
4. Start the executable directly with `ProcessStartInfo.ArgumentList`; capture diagnostic output.
5. Record ownership, then wait for authenticated version and session responses.

We start with a controlled executable path and configuration argument. The secret lives in the protected configuration, avoiding a command-line secret. Ownership metadata records both PID and process start time because Windows can reuse a PID after a process exits.

## Lesson 2: RPC is a request to another process

RPC means remote procedure call. Here the other process is on the same computer. `Aria2RpcClient` posts JSON to `http://127.0.0.1:16800/jsonrpc` and receives JSON back. This is not a web service we deploy elsewhere.

A simplified health request looks like:

```json
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "aria2.getVersion",
  "params": ["token:<generated secret>"]
}
```

The method names the engine action. The ID lets us match a response to its request. The token authenticates the caller. A second request, `aria2.getSessionInfo`, identifies this engine session. Subsequent checks verify that its version and session still match.

The client deserializes replies into typed C# records and maps failures to `EngineFailure` values. It never displays raw remote error text, which might contain credentials or sensitive URLs. See the official [aria2 RPC documentation](https://aria2.github.io/manual/en/html/aria2c.html#rpc-interface) for the protocol.

Integration tests exposed two details worth learning from: aria2 expects an HTTP Content-Length header, so we serialize into `StringContent`; authentication errors arrive as JSON-RPC errors in an HTTP 400 response, so we inspect that envelope before classifying the failure. Both cases now have regression coverage.

## Lesson 3: started does not yet mean ready

`Process.Start` only tells us Windows launched the executable. The engine could fail immediately, take time to open its port, or reject authentication. We show Ready only after a successful authenticated handshake with the pinned version.

`async` and `await` let us wait for I/O without blocking the Windows UI thread. They do not automatically create a new thread for every operation. A `CancellationToken` lets Close cancel startup or a health check. Timeouts bound how long we wait: startup defaults to 12 seconds, each request to 2 seconds, and shutdown to 5 seconds before the owned-process fallback.

A semaphore serializes lifecycle operations, so a retry cannot start a second owned engine while shutdown is running. A single non-overlapping two-second timer checks health after startup. An unexpected process exit publishes a failure promptly; restart is manual through Start engine.

## Lesson 4: how the status reaches XAML

Trace the Start engine button through these files:

```text
DownloadsPage.xaml → EngineViewModel.StartCommand
  → EngineLifecycleService.StartAsync
  → IManagedAria2Engine → Aria2ProcessManager → Aria2RpcClient
  → EngineStateStore publishes Starting / Ready / Failed
  → UI dispatcher → EngineViewModel → XAML bindings
```

`EngineLifecycleService` decides what the application reports. Infrastructure handles OS/network details. `EngineStateStore` publishes immutable snapshots with increasing revisions. `EngineViewModel` sends each update to the UI dispatcher and ignores older queued revisions. The page itself never starts a process or sends RPC.

Start engine is enabled after a failure, and Check connection is enabled when Ready. The spinner follows Starting/Stopping. This is the same MVVM pattern you learned for simulated progress in Phase 1.

## Lesson 5: shutdown is part of the feature

`App.OnWindowClosing` initially cancels the window close so cleanup can finish. It disables the page, cancels pending commands/timers, and waits for them before stopping and disposing services.

`StopOwnedCoreAsync` asks aria2 to save its session, then requests shutdown and waits for the process to exit. If it misses the deadline, we terminate only our owned process handle. We remove this run's configuration and ownership record, retain its checkpoint, release the leases, and finally close the window.

We never stop a process merely because its name is aria2 or because it holds our preferred port. A surviving recorded engine is refused for now; Phase 5 will implement recovery and the final Close-to-tray/Exit policy. The current checkpoint is not connected to SQLite or automatic resume.

## Files to read in order

| File | What it teaches |
| --- | --- |
| `App.xaml.cs` | Startup composition, lifetime cancellation, delayed window close |
| `Application/Engine/EngineContracts.cs` | Typed boundaries and failure vocabulary |
| `Application/Engine/EngineLifecycleService.cs` | Reporting state around asynchronous operations |
| `Infrastructure/Aria2/Aria2ProcessManager.cs` | Ownership, startup checks, handshake, shutdown |
| `Infrastructure/Aria2/Aria2RpcClient.cs` | HTTP, JSON requests, typed responses, timeouts |
| `App/ViewModels/EngineViewModel.cs` | Commands, UI dispatch, observable status |
| `tests/AriaGui.Infrastructure.Tests/ProcessTests.cs` | Real-process evidence and failure scenarios |

The production paths in the table are relative to `src`. For debugging, set a breakpoint in `EngineLifecycleService.StartAsync` and another just before `_rpc.GetInfoAsync` in the process manager. Follow execution until the Ready snapshot is published. Do not inspect or share the generated secret in a screenshot.

## Verification record — 3 October 2026

| Check | Result |
| --- | --- |
| Locked dependency restore and full x64 solution build | Passed; zero warnings/errors |
| Unpackaged app build and launch | Passed; zero warnings/errors |
| Core tests | 16 passed |
| Application, simulation, and actual ViewModel tests | 20 passed |
| Infrastructure/RPC/process tests | 21 passed |
| Pinned release acquisition script | Downloaded official asset, verified archive and executable hashes, retained notices |
| Real readiness, version, and session identity | Passed in integration tests and the running app's redacted diagnostics |
| UI-bound status | Native accessibility tree reports Ready, aria2 1.37.0, disabled Start, enabled Check connection |
| Second native app launch | Exited with code 0; original GUI PID and owned engine PID unchanged |
| Wrong secret, missing/modified binary, occupied port | Passed; conflicting endpoint was not contacted or stopped |
| Startup deadline, early exit, cancellation | Passed; partially started owned children cleaned up |
| Engine crash and restart | Passed; failure event emitted and new session created |
| Surviving ownership record | Refused without touching the recorded process |
| Private folder permissions and exclusive instance lease | Passed, including separate storage-failure reporting |
| Graceful engine shutdown | Integration test confirms session saved, process exit code 0, secret configuration/metadata removed |
| Final native Check/Start/Close click-through and visual layout | Pending: Windows is locked; native actions cannot activate the window |
| Packaged launch / debugger exercises | Still pending from Phases 0–1; see `WALKTHROUGH.md` |

Run `scripts/Verify.ps1` to repeat the automated checks. Reports are written under ignored `artifacts/test-results`. Integration tests use isolated temporary folders and ports, not the running app's data. The running unpackaged preview has been left open with its engine Ready for review after unlocking Windows.

To finish the manual smoke check: click Check connection and confirm Ready persists; close the window and confirm both `AriaGui.App.exe` and its owned `aria2c.exe` exit; launch again and confirm Ready returns. The automated crash/restart and shutdown tests already cover the backend behavior. Visual layout and button/Close interaction remain explicitly unverified for the final native build.

## Next phase

Phase 3 will make Add create a real transfer, poll real download observations, and save jobs/attempts/files/history in SQLite. We will record command intent before sending a mutation so an uncertain RPC result can later be reconciled. It will build on this verified connection rather than changing the XAML into direct engine calls.
