# How AriaGUI works

[← Back to the README](../README.md) · [Developer setup](DEVELOPMENT.md)

AriaGUI combines a native Windows interface with a separately managed aria2 process. The interface handles user actions; the application layer coordinates work; the infrastructure layer talks to aria2, stores history, and manages files.

This guide describes the source checkout. The [release notes](RELEASE_NOTES.md) describe a particular package and may cover fewer features.

## The overall flow

```text
User action in the WinUI interface
                 |
                 v
View models and application services
         |                       |
         v                       v
SQLite history / preferences   Managed aria2 client
                                 |
                                 v
                         Private local JSON-RPC
                                 |
                                 v
                           aria2c.exe
                                 |
                                 v
                     Transfer progress and files
                                 |
                                 v
                  Reconciliation and UI updates
```

The app owns the lifecycle of its engine. You do not need to start an aria2 server yourself or enter an RPC secret during normal setup.

## Where the code lives

| Folder / project | Responsibility |
| --- | --- |
| `src/AriaGui.App` | WinUI views, view models, theme dictionaries, tray behavior, Windows notifications, and application composition. |
| `src/AriaGui.Core` | Download models, statuses, filename rules, and bounded torrent metainfo parsing. |
| `src/AriaGui.Application` | Download coordination, queue and bandwidth policy, lifecycle decisions, notification policy, and external intake contracts. |
| `src/AriaGui.Infrastructure` | aria2 process/RPC management, guarded HTTP transfers, torrent publication, SQLite persistence, and browser transport. |
| `src/AriaGui.Browser.Contracts` | Browser handoff schema and shared protocol types. |
| `src/AriaGui.NativeHost` | Console entry point for browser native messaging. |
| `extension` | Chrome/Edge download-handoff extension source used by the Chrome package scripts. |
| `extensions/aria-gui` | Separate cross-browser public-link development MVP and experiments. |
| `tests` | Focused test projects, browser contract fixtures, and process/UI fixture applications. |
| `scripts` | Build/run helpers, packaging, diagnostics, browser tooling, and verification scripts. |
| `vendor/aria2` | Pinned engine executable, provenance, and upstream notices. |

The dependency direction is intentional: Core defines models, Application defines workflows and interfaces, Infrastructure implements external operations, and App composes them. `App.xaml.cs` registers services; `DownloadsPage` composes the download UI. These are integration-owned files under the repository rules.

## Starting the app and engine

1. **Resolve application data.** The app selects the runtime directory appropriate to its package identity.
2. **Establish ownership.** A per-user application lease prevents competing owners of the same runtime directory. Activation can bring an existing window forward.
3. **Compose services.** Dependency injection connects storage, the managed engine client, download services, notifications, preferences, and view models.
4. **Prepare the engine.** The manager validates the bundled executable against its pinned hash and prepares protected runtime files.
5. **Connect privately.** aria2 uses loopback JSON-RPC on port `16800`, with a generated secret and remote listening disabled.
6. **Reconcile state.** Saved records and engine observations are brought together before normal updates continue.

If the port is occupied by another program, startup reports a conflict. The app does not take over or terminate an unrelated process to claim the port.

Useful starting points: [application composition](../src/AriaGui.App/App.xaml.cs), [engine options](../src/AriaGui.Infrastructure/Aria2/Aria2ProcessOptions.cs), and [process manager](../src/AriaGui.Infrastructure/Aria2/Aria2ProcessManager.cs).

## Adding an HTTP download

The standard application workflow currently accepts HTTP/HTTPS URLs. Some UI hints mention FTP, but the HTTP coordinator rejects other schemes; those hints should not be taken as end-to-end protocol support.

1. The Add dialog collects URLs and a destination. It checks input before invoking the view model.
2. `DownloadCoordinator` validates the request, destination, optional filename, and any requested checksum/connection settings. Embedded URL credentials are rejected.
3. The repository saves **durable download intent before sending a mutating command to aria2**. This gives recovery a record to reconcile if execution is interrupted.
4. The managed engine client submits the transfer and associates its engine identity with the saved job.
5. Progress observations update the runtime store. The UI receives dispatched updates rather than performing engine work itself.
6. Completion, failure, and subsequent actions are reconciled with persisted state.

See [DownloadCoordinator](../src/AriaGui.Application/Downloads/DownloadCoordinator.cs) and the [download view model](../src/AriaGui.App/ViewModels/DownloadsViewModel.cs).

### Guarded HTTP, verification, and parallel connections

The guarded backend uses private staging and a local continuity gateway to control transfer and publication. Supplying an expected SHA-256 makes successful publication depend on the staged file matching that digest.

The coordinator accepts connection choices of **1, 2, or 4**. More than one connection requires an expected SHA-256 and backend support. The origin must also satisfy the guarded range-download requirements, including a stable representation and usable byte ranges. Server and workflow eligibility matter; these options are not a promise that every link can be accelerated.

Backend capability and visible UI are separate: a dialog may expose fewer options than the coordinator accepts. For implementation details, start with [the continuity relay](../src/AriaGui.Infrastructure/Downloads/HttpContinuityRelay.cs) and [the feature architecture](ARIA2_FEATURE_ARCHITECTURE.md).

## Torrent intake and file publication

The current Add workflow can read a **local BitTorrent v1 `.torrent` file**. The metainfo parser is bounded and rejects BitTorrent v2 and hybrid torrents.

The dialog presents a file tree, selection summary, destination checks, and a privacy/sharing notice. Saving the selection creates a persisted torrent draft; dispatch to the engine is a separate step. Space checks account for both private payload storage and selected files that will be published to the destination.

The infrastructure layer observes torrent progress and publishes selected files through its torrent publication path. This is distinct from the HTTP checksum workflow. Do not infer magnet-link intake or support for every aria2 torrent option from the engine's capabilities.

Start with [torrent metainfo parsing](../src/AriaGui.Core/Downloads/TorrentManifest.cs), [the Add dialog](../src/AriaGui.App/Views/Downloads/AddDownloadDialog.xaml.cs), and [torrent publication](../src/AriaGui.Infrastructure/Downloads/TorrentPublisher.cs).

## Queue, bandwidth, and progress updates

`DownloadCoordinator` coordinates queue actions and stores policy through the download repository. Saved settings include aggregate bandwidth policy, per-job limits, and recurring schedules. Policy holds and temporary overrides affect whether work should run.

Runtime stores provide the UI with current snapshots. Progress queries are batched, inventory reconciliation happens less frequently, and idle work backs off. History browsing reads bounded pages from SQLite rather than loading the full history into the live transfer list.

See [bandwidth coordination](../src/AriaGui.Application/Downloads/DownloadCoordinator.Bandwidth.cs), [schedule coordination](../src/AriaGui.Application/Downloads/DownloadCoordinator.Schedule.cs), and [the runtime store](../src/AriaGui.Application/Downloads/DownloadRuntimeStore.cs). [Phase 6](PHASE_6.md) records the original update and scale design.

## Closing, quitting, and recovery

**Close** normally hides the window to the tray. The application and engine continue running. If tray registration fails, the app keeps its window reachable.

**File → Exit** runs the explicit shutdown path: checkpoint state, request engine shutdown, and exit. A shutdown timeout can terminate only the owned or fully verified adopted engine process.

After an interruption, startup can adopt a surviving engine only after validating the recorded process identity and protected session/configuration state. Otherwise, validated checkpoints can help restore known unfinished jobs into a new engine. Unknown records are preserved and reported rather than silently treated as owned work.

Recovery depends on the transfer and saved state; a checkpoint does not guarantee that every server supports resuming. See [the recovery planner](../src/AriaGui.Infrastructure/Aria2/SessionRecoveryPlanner.cs) and [the recovery design](PHASE_5.md).

## Data storage

For an **unpackaged or portable** app, runtime data lives under:

```text
%LOCALAPPDATA%\AriaGUI\Engine
```

For an app with **package identity**, `AppDataPaths` uses the package's local application-data folder with an `Engine` subfolder. These profiles are separate; the app does not automatically import one into the other.

| Data | Purpose |
| --- | --- |
| `downloads.db` | Download records, queue/bandwidth policy, torrent metadata, notifications, and persisted operation state. |
| `preferences.json` | Validated user preferences, including theme, destination, layout, and feature settings. |
| `sessions/` and checkpoint metadata | Retained engine state for recovery. |
| `aria2.conf` | Generated engine configuration containing a private RPC secret; removed after clean shutdown. |
| `engine.owner.json` | Engine ownership metadata used during lifecycle validation. |
| `Backups/` | Private database backups created before schema upgrades. |
| `engine.log` and `engine.log.previous` | Bounded engine diagnostics. |

Settings and history are separate from the executable directory and completed downloads. Removing a portable app folder does not remove this data. Do not run packaged and portable builds together: although their profiles differ, both use the same engine RPC port.

Database upgrades use versioned EF Core migrations and create a verified backup before upgrading an existing schema. Unknown newer schemas are rejected. See [the schema upgrade implementation](../src/AriaGui.Infrastructure/Persistence/DatabaseSchemaUpgrade.cs).

Treat runtime files as private: saved URLs and session data can contain sensitive information. Do not attach the runtime folder or `aria2.conf` to a public issue. Review any diagnostic export before sharing it.

## Browser handoff

There are two browser-related source paths in this repository:

| Path | Role |
| --- | --- |
| `extension/` | Current Chrome/Edge handoff source, with a native-messaging connection, capture controls, and package scripts. |
| `extensions/aria-gui/` | Separate development MVP for explicit public-link submission, plus experiments and shared contract checks. |

For the Chrome unpacked workflow, the user registers the host from **Settings → Integration**, loads the extension, checks its connection, and opts into capture in both places. Native messaging provides the browser-to-desktop bridge; the app then validates and admits eligible requests into its download workflow. Registration records the current host/app paths, so moving the app requires registration again.

The unpacked extension is intended for local testing and does not update automatically. A successful handoff means the app accepted work, not that the transfer has completed. The stable portable ZIP does not include the extension; development tooling does not establish production support across browsers.

Use [the current unpacked installation instructions](../extension/INSTALL-unpacked.txt) for that workflow. The [older MVP README](../extensions/aria-gui/README.md), [native host README](../src/AriaGui.NativeHost/README.md), and [companion installation record](BROWSER_HANDOFF_INSTALLATION.md) describe separate paths and their limits; read them in that context.

## Appearance and accessibility

The UI uses WinUI views and shared dictionaries under `src/AriaGui.App/Themes`. Light, dark, and High Contrast resources provide theme tokens, while shared styles reference those tokens.

Repository rules require keyboard focus to remain usable and icon-only controls to have both a tooltip and an accessible name. UI contributors should use existing tokens rather than hard-coded styling in views. Theme resources and accessible properties still require build-and-launch verification; they do not by themselves certify every assistive-technology scenario.

## Continue reading

- [Developer guide](DEVELOPMENT.md): exact toolchain, launch profiles, debugging, diagnostics, and migrations.
- [Release notes](RELEASE_NOTES.md): the portable package's behavior and limitations.
- [Release acceptance](RELEASE_ACCEPTANCE.md): package and release checks.
- [Third-party distribution record](THIRD_PARTY.md): pinned components, notices, and redistribution status.
