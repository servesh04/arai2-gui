# Phase 3: a real download with durable history

Historical Phase 3 lesson and verification record. [Phase 4](PHASE_4.md) now adds durable controls, linked retry attempts, queue order, and saved concurrency.

## What is working

Paste an HTTP/HTTPS URL, choose a folder with Browse or type its absolute path, edit the suggested filename, and click Download. The app now sends a real `aria2.addUri` request. The list shows observed progress, speed, size, and completion; completed and failed records survive app restart.

The default destination is `Downloads/AriaGUI` under your Windows user profile. It is created on Add. Existing files, `.aria2` control files, directories at the target path, and destinations already recorded in history are rejected. Choose a new filename or folder. This phase does not offer overwrite, automatic renaming, or history deletion.

Interrupted transfers show Needs recovery when their saved ID is absent from the new engine session. We keep their intent and last checkpoint without resubmitting. Full resume/recovery is Phase 5 work. Pause/resume/removal/retry controls are Phase 4 work.

## Lesson 1: a table stores data after the process exits

Phase 1 kept everything in memory. Now `SqliteDownloadRepository` writes `%LOCALAPPDATA%\AriaGUI\Engine\downloads.db`, inside the protected per-user folder. Closing the window no longer erases history.

SQLite is an embedded database file, not a separately installed server. EF Core maps C# entities to its tables. Our database entities are deliberately different from the immutable records that the application and ViewModels use.

| Table | What it stores | Why it is separate |
| --- | --- | --- |
| Jobs | User task, original URL, output path, creation time | User identity remains stable across future retries |
| Attempts | Engine GID, job link, last observed status and bytes, update time | A future retry gets its own transfer attempt |
| Files | Output filename, job/attempt links, known size | Leaves room for future transfers with multiple files |
| Operations | Add intent, attempt link, requested time, Pending/Uncertain/Confirmed/Failed | Records what we asked for even if a reply is lost |

The job's destination is the full output path. A unique normalized destination key prevents two records reserving the same Windows path. A unique attempt GID prevents engine identity reuse. Foreign keys keep links between these tables valid.

URLs, including query parameters, are persisted because the transfer needs its source. Treat the database and session files as private app data. Their folder is access-restricted; the database is not encrypted. UI rows display the hostname, and diagnostics omit raw RPC errors and redact URLs/secrets.

## Lesson 2: a migration versions the schema

Open `src/AriaGui.Infrastructure/Persistence/DownloadDbContext.cs`, then its `Migrations` folder. `InitialDownloads` creates the four tables and their constraints. The model snapshot records what EF generated, so future migrations can compare against it.

At initialization we call `Database.Migrate()`. EF applies missing migrations and records them in its migration history. We use migrations rather than recreating the database on launch. A corrupt database is reported and preserved, not silently deleted or reset. See Microsoft's [migration overview](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/) for the mechanism.

The design-time factory lets migration tooling create a context without launching WinUI or opening the user's database. The generated migration and snapshot belong in source control. `.tools/ef` contains the ignored local generation tool; it is not required for ordinary build/run.

## Lesson 3: trace Download through the layers

```text
DownloadsPage.xaml: Download button
  → DownloadsViewModel.AddDownloadCommand
  → DownloadCoordinator.AddAsync
  → FileDownloadDestination: validate path and test folder write access
  → SqliteDownloadRepository.InsertIntentAsync: transaction commits four records
  → ManagedAria2Client → Aria2ProcessManager → Aria2RpcClient
  → aria2.addUri with our preallocated GID, directory, and filename
  → confirm observation in SQLite, then publish to DownloadRuntimeStore
  → UI dispatcher → list/summary ViewModels → XAML bindings
```

The folder picker is an App service; it returns a path through `IDownloadFolderPicker`. The ViewModel does not directly inspect files, send RPC, or create a database context. The Windows implementation uses the App SDK picker with the owning window ID; see [Microsoft's picker guide](https://learn.microsoft.com/en-us/windows/apps/develop/files/using-file-folder-pickers).

The destination adapter rejects unsafe Windows filenames and probes write access before recording intent. aria2 also receives no-overwrite/no-renaming options, an explicit output filename, certificate checking, and disabled automatic torrent/Metalink following. This phase supports ordinary HTTP/HTTPS files.

In SQLite, one `SaveChanges` transaction commits the job, attempt, file, and pending operation together. If that commit fails, no Add is sent. The test suite verifies this ordering and verifies rollback when a uniqueness constraint fails.

## Lesson 4: a lost response is not a rejected request

Before Add, we generate a nonzero 16-digit hexadecimal engine ID and save it with the attempt. The application job ID is a separate GUID. The JSON-RPC request ID is separate again: it matches a request to its reply, while the GID identifies a transfer.

Suppose aria2 accepts Add, starts downloading, and its reply is lost. Sending another Add could create a duplicate. Instead, the coordinator persists Uncertain and later asks `aria2.tellStatus` about the saved GID. Finding it confirms the original request without resubmitting. Not finding it surfaces Needs recovery and preserves the intent.

Confirmed Add means the engine accepted the transfer. It does not mean the file completed. Completion comes from a later observation. A definite engine rejection marks the operation Failed; cancellation after intent also preserves uncertainty because cancellation alone cannot prove what happened outside the app.

There is no shared transaction across SQLite and aria2. We commit intent first and reconcile their separate outcomes. The controlled lost-reply test accepts a real download, injects a lost response, recreates the coordinator from SQLite, and verifies one Add, one HTTP request, and one completed job.

## Lesson 5: keep I/O off the UI thread

`SqliteDownloadRepository.OnDatabaseThreadAsync` is the one database execution boundary. It serializes database work, runs synchronous SQLite operations in a background task, and creates/disposes a fresh `DownloadDbContext` for each unit of work. Contexts are never shared across threads. Microsoft's [SQLite async documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async) explains why adding `Async` to a database call does not itself make its I/O asynchronous.

The coordinator has one non-overlapping 500 ms polling loop. It queries only the application's known nonterminal GIDs. Network waits happen outside its state-update gate; an in-flight Add is not mistaken for a missing transfer by an early poll. Terminal states cannot regress to an older active observation.

Live progress stays in memory. Status changes, known-size changes, operation outcomes, and errors are saved promptly; ordinary byte progress is checkpointed at five-second intervals while it changes. We do not write SQLite every UI refresh. On reopening, saved speed is zero until new observations arrive.

## Files to read in order

| File under `src` | Responsibility |
| --- | --- |
| `AriaGui.App/ViewModels/DownloadsViewModel.cs` | Form, filename suggestion, Add/Browse commands, visible errors |
| `AriaGui.Application/Downloads/DownloadCoordinator.cs` | Durable ordering, observations, uncertainty, checkpoints |
| `AriaGui.Application/Downloads/DownloadStorageContracts.cs` | Repository/destination interfaces and safe errors |
| `AriaGui.Infrastructure/Downloads/FileDownloadDestination.cs` | Windows path/name/collision/write checks |
| `AriaGui.Infrastructure/Persistence/DownloadDbContext.cs` | Tables, constraints, design-time factory |
| `AriaGui.Infrastructure/Persistence/SqliteDownloadRepository.cs` | Transactions, entity mapping, background execution |
| `AriaGui.Infrastructure/Aria2/Aria2RpcClient.cs` | Typed Add/tellStatus wire mapping and sanitized errors |
| `AriaGui.App/App.xaml.cs` | Real-client registration, history initialization, cancellation/shutdown |

For a debugger exercise, break just before `InsertIntentAsync`, then before `engine.AddAsync`. Inspect the job ID, GID, target path, and pending operation. Continue to `ApplyObservation`, then to `DownloadItemViewModel.Update`. Those are the steps from a click to saved state and visible progress. The breakpoint exercise remains a learner task, not claimed as an automated test.

## Verification record — 4 October 2026

| Check | Result |
| --- | --- |
| Locked full x64 solution build | Passed, zero warnings/errors |
| Automated tests | 76 passed: Core 16, Application 24, Infrastructure 36 |
| Fresh migration / repeat initialization / schema snapshot | Passed; no pending model changes |
| Multi-table intent transaction | Commits before RPC; failure/constraint rollback leaves no partial job |
| Real known-file HTTP download | Completed; downloaded SHA-256 matches fixture payload |
| HTTP 404 / empty file / chunked unknown-length file | Correct terminal outcomes saved; unknown active total observed |
| Completed history with a fresh repository and engine | Same job restored, no new HTTP request |
| Lost real Add reply | Uncertain intent restored and reconciled without another Add |
| Cancellation / concurrent Add / in-flight polling | Intent retained, no duplicate submission or lost jobs |
| Existing destination / unsafe filename / denied write access | Useful error; no engine mutation; existing bytes preserved |
| Corrupt database | Reported and preserved unchanged |
| Native folder picker | Opened and selected `artifacts/native-download-smoke` |
| Native HTTP download | Completed; fixture hash `31A1F9DEA0169551092D05E8BF4A446228C8C3EB4C9B713C66ADCB7FD53C89BE` matched |
| Native internet HTTPS download | Official aria2 COPYING completed; hash matched bundled notice |
| Native existing-file error | Readable message, download count unchanged |
| Native Check connection / Close / reopen | Ready persisted; owned engine exit code 0; completed history restored |

`scripts/Verify.ps1` repeats the automated checks and writes ignored TRX reports under `artifacts/test-results`. The tests isolate databases, download folders, and child engines in temporary folders. They never use the running app's database or user download files.

Dependency restore originally warned about EF's older native SQLite bundle. The explicit `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 pin replaces it; locked restore/build now has no warning, and an integration test checks the native SQLite version is at least 3.50.2. Keep this pin and rerun the tests when updating database dependencies.

## Repeat the native fixture exercise

From the repository root, start the controlled server in a separate terminal:

```powershell
dotnet run --project tests/AriaGui.EngineFixture -- --http-fixture
```

It prints a loopback URL with a dynamically selected port. Run `scripts/Run.ps1 -Unpackaged`, paste that URL, select a test folder, and use a fresh filename for each exercise. Wait for Completed, close the app, reopen, and find the saved record. Stop the fixture terminal with Ctrl+C when finished. The test helper's normal mode still supports Phase 2 timeout/early-exit tests; it is not shipped with the app.

## Next phase

Phase 4 adds durable Pause/Resume/Remove/Retry commands and queue behavior. Phase 5 will reconcile interrupted transfers with engine session/control files and introduce Close-to-tray versus Exit. This phase preserves the data those features need without promising automatic recovery yet.
