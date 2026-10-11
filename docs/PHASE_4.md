# Phase 4: commands and the engine queue

Implemented on 4 October 2026. Automated verification passes **96 tests**: 16 Core, 37 Application, and 43 Infrastructure. Packaged and unpackaged builds pass with zero warnings or errors. Native inspection of the new buttons was interrupted when Computer Use was stopped; it is still a manual check, not claimed as passed.

## What you can do now

| Control | Behavior |
| --- | --- |
| Pause | Request a pause for an active or waiting transfer; confirm its actual state |
| Resume | Make a paused transfer eligible to run, retaining its attempt and GID |
| Remove | Stop an active/waiting/paused transfer; retain history, output, and control files |
| Retry | Create a new attempt for a reconciled failed or removed job, with a new GID and a link to the previous attempt |
| Up / Down arrows | Move a waiting download by one queue position; active downloads continue |
| Simultaneous downloads / Apply limit | Set and save an engine concurrency limit from 1 to 16; default 3 |

Completed history stays completed and has no Remove or Retry action. Retry is a manual action, with no application retry loop. aria2 still has its bounded network retries. A file without its control file is protected from Retry; the error suggests keeping it and adding a different filename. A partial file with its control file can be continued by aria2. Resume and Retry are different operations.

After a successful Add, the Add form collapses to give the list more room. Expand it to add another URL or edit the concurrency limit. Rows show pending actions, waiting position, and enabled controls. Error and completion notices stay above the list. Connection loss disables controls and leaves the observed transfer status intact.

Close still performs Exit. A paused or active transfer absent from a fresh engine session shows Needs recovery. Restoring engine sessions and adopting a surviving engine are Phase 5 work; this phase does not resubmit uncertain or interrupted transfers automatically.

## Lesson 1: requested state and observed state are different

Think of Pause as a request to another program. The app can show Pausing immediately, but it must wait for evidence before calling the transfer Paused.

```text
Click Pause
  → DownloadItemViewModel.PauseCommand
  → DownloadsViewModel: handle user-facing errors
  → DownloadCoordinator.PauseAsync → CommandAsync
  → SQLite: commit PendingOperation(Pause)
  → publish Pausing… while retaining the observed progress
  → ManagedAria2Client / Aria2ProcessManager
  → aria2.pause, saveSession, tellStatus
  → SQLite: save observed status and confirm the operation
  → shared store → UI dispatcher → updated row
```

If a response is lost, the operation becomes Uncertain. A later poll checks that same GID. Observing Paused confirms Pause; observing the old Downloading state does not confirm it. Resume requires waiting/active evidence. Remove requires removal evidence, or an observed terminal outcome that supersedes the request. A missing GID does not prove successful removal.

Completion can race with Pause. If the command's response observes completion, Completed wins. If aria2 rejects a command because the transfer has already completed, the rejected operation is retained in the database and polling obtains the actual outcome. The download is not marked Failed merely because a command failed.

## Lesson 2: two kinds of locks solve different problems

Open `src/AriaGui.Application/Downloads/DownloadCoordinator.cs`.

The short state gate protects shared snapshots, checkpoints, and database commits. Network waits happen outside it. A separate semaphore per job serializes conflicting commands for that job. A queued Resume therefore waits for Pause; a second Pause sees the confirmed Paused state and sends no duplicate mutation. Retry clicks are coalesced while a new attempt is being created.

The coordinator also tracks in-flight job IDs. Polling cannot classify a job as missing while Add or a command is still running. The engine manager has its own process/RPC gate so start, shutdown, and requests cannot operate on a disposed connection.

These semaphores do not schedule downloads. They coordinate our calls and state updates; aria2 remains the scheduler.

## Lesson 3: old observations must not undo new evidence

A poll can capture Downloading, then return after Pause has already been confirmed. Applying that poll would make the screen lie.

Each poll captures the coordinator's observation epoch and gets a monotonically increasing sequence. Starting/finishing a mutation invalidates earlier observations. A poll is ignored if its epoch changed or a newer poll already applied. Real observation batches carry aria2's session ID; changing sessions invalidates older batches. The manager verifies the authenticated session before returning observations or performing commands.

Terminal transitions are also guarded. An old active observation cannot undo Completed, Failed, or Removed for the same attempt. Retry deliberately installs a different attempt, rather than resetting the old attempt's terminal status.

This is an initial connection-generation boundary. Phase 5 adds the broader recovery policy around engine adoption and session restoration.

## Lesson 4: queue order belongs to aria2

`Aria2RpcClient` reads the waiting queue in pages and the manager attaches observed positions to the app's own GIDs. The app displays active rows first, then waiting rows by position, then paused/history rows, preserving each row ViewModel's identity as it moves.

An arrow click becomes an absolute target position saved with its operation. We send `aria2.changePosition` using `POS_SET`. Reconciliation checks the observed position, or recognizes that the transfer has already started/stopped. Priority changes do not pause active transfers. Paused transfers remain in aria2's waiting inventory and can occupy positions, but their arrow controls are disabled until resumed.

Concurrency is an engine option, not another application scheduler. `aria2.changeGlobalOption` applies it, and `getGlobalOption` verifies it. Reducing the limit is delegated to aria2; it controls which eligible transfers run. The app does not start a second loop to pause/start jobs itself.

The new QueuePolicies table stores the last confirmed limit and an optional pending/uncertain request with its ID and requested value. A confirmed saved limit is applied once when connecting to a fresh session if needed. An uncertain setting is checked against the engine and is not blindly sent again. General settings/history editing can be added later.

## Lesson 5: Retry preserves the user's job

Read `RetryAsync`, then `SqliteDownloadRepository.InsertRetryIntentAsync`.

1. Wait for conflicting commands on the job.
2. Require a failed/removed attempt without an unresolved operation.
3. Query the old GID and refuse if the old transfer is still active, waiting, or paused.
4. Validate the same destination. Keep existing files; reject a file missing its control file.
5. Atomically insert the new attempt, file record, and pending Retry operation. The job is unchanged and the old attempt remains terminal.
6. Send Add with a new preallocated GID. Enable continuation only when a control file was found; overwrite and automatic renaming stay disabled.
7. Confirm the new attempt or retain uncertainty and reconcile its new ID.

The new attempt's ParentAttemptId points to the previous attempt. StartedAt is strictly later so history loading reliably selects the latest attempt. A failed HTTP attempt followed by Retry produces one job and two attempts, rather than two unrelated download rows. Redownload from scratch and file deletion are not offered by these controls.

## Lesson 6: completion notices have a durable identity

`SaveAndReportCompletionsAsync` compares the stored attempt status to the new status before saving. It returns an attempt ID only when that database transaction first changes the attempt to Completed. The coordinator publishes an in-app completion notice after the commit.

Repeated polls and reopening completed history produce no new notice for that attempt. If the app crashes after the commit but before showing the notice, the notice can be missed; this is at-most-once notification behavior, not guaranteed delivery. Windows toast notifications are not implemented here.

## Lesson 7: migrate data instead of replacing it

`Persistence/Migrations/*QueueCommands*` adds the operation's requested queue position and the QueuePolicies table. InitialDownloads is unchanged. Startup applies the new migration to an existing Phase 3 database without erasing jobs or attempts.

The upgrade test creates a database at the original migration, inserts completed history, applies the new migration, and verifies the record survives and the current model needs no migration.

## Read and debug in this order

| File | Focus |
| --- | --- |
| `src/AriaGui.App/Views/DownloadsPage.xaml` | Row command bindings, Add expander, concurrency editor |
| `src/AriaGui.App/ViewModels/DownloadItemViewModel.cs` | Command availability and requested-action labels |
| `src/AriaGui.App/ViewModels/DownloadsViewModel.cs` | Error handling, row ordering, command cancellation |
| `src/AriaGui.Application/Downloads/DownloadCoordinator.cs` | CommandAsync, RetryAsync, RefreshAsync, SetConcurrencyAsync |
| `src/AriaGui.Infrastructure/Persistence/SqliteDownloadRepository.cs` | Intent commits, retry transaction, completion receipt |
| `src/AriaGui.Infrastructure/Aria2/Aria2RpcClient.cs` | Typed aria2 commands and queue/settings queries |

Set breakpoints in CommandAsync before InsertOperationAsync, at ExecuteAsync, and at ApplyObservation. Use a slow local download. Inspect the operation's action/status alongside the observed progress. Continue and watch Pausing become Paused. This debugger exercise is a learning instruction, not a completed verification claim.

## Verification record

| Check | Result |
| --- | --- |
| Full locked restore, packaged build, all tests | Passed: 96 tests, zero warnings/errors |
| Latest unpackaged preview build and launch | Build passed; launch invoked; new native controls not inspected |
| Per-job serialization, duplicate Pause/Retry, rapid Pause → Resume | Passed |
| Intent storage failure | No engine mutation sent |
| Lost accepted Pause reply / unaccepted Resume timeout | Reconciled correctly, with no duplicate command |
| Old poll across command or changed engine session | Cannot overwrite newer evidence |
| Completion racing Pause; completion notice repeat/restart | Terminal state preserved; no duplicate notice |
| Real aria2 pause/resume, Remove, partial Retry | Passed; final bytes match fixture; old attempt/history preserved |
| Real aria2 concurrency limit and waiting reorder | Passed; active transfer not preempted by reorder |
| Real HTTP failure → new retry attempt | Passed; same job, new GID/parent link, correct bytes |
| Phase 3 → Phase 4 database upgrade | Passed; completed history retained |
| Retry with existing file but missing control file | Rejected; bytes unchanged |
| Native row buttons / new layout | Pending: Computer Use was stopped before inspection |

Run `scripts/Verify.ps1` to repeat automated checks. They use isolated temporary folders, databases, and engines, with a local HTTP server supporting slow, ranged, failed, and partial responses. They do not mutate the running preview's downloads.

For a manual demonstration, start `dotnet run --project tests/AriaGui.EngineFixture -- --http-fixture`. Replace the printed URL's path with `/slow`, select a test folder and fresh filename, and Download. Pause promptly, then Resume. To test queue order, set limit 1 and add three slow downloads quickly. Expand the Add form to edit the limit. Stop the helper terminal with Ctrl+C afterwards. Do not run the rebuilding tests while that helper executable is in use.

## Next phase

Phase 5 restores transfers after interruption, reconciles preserved session/control files, and implements tray Close versus explicit Exit. Needs recovery remains truthful until that policy is implemented.

Primary reference: the [aria2 RPC manual](https://aria2.github.io/manual/en/html/aria2c.html#rpc-interface) defines pause/unpause/remove, waiting inventory, queue position, global options, and continuation behavior. Our ownership, intent, UI, and recovery boundaries are application design decisions.
