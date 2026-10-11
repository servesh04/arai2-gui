# Browser handoff implementation plan for GPT-6.1 Sol

Status: planning only, 2026-10-11. No runtime feature was implemented by this review. Normative design choices are in [architecture](BROWSER_HANDOFF_ARCHITECTURE.md); proposed wire/state semantics and acceptance IDs are in [protocol and tests](BROWSER_HANDOFF_PROTOCOL_AND_TESTS.md). Re-read the current repository before coding because other work can change these source paths and contracts.

## 1. Scope and working rules

Deliver a narrow end-to-end public HTTP(S) GET context-menu handoff first. Every request travels through the app's durable coordinator/history pipeline. The browser does not know aria2's port/token. Host installation and cross-process error handling are part of the feature, not cleanup after UI work.

Do not implement cookies, arbitrary headers, automatic cancellation, history erasure, protocol URL import or partial browser-file takeover in the initial slice. Do not change existing queue/recovery behavior merely to make a browser demo work. A useful small implementation with explicit boundaries is preferable to a blanket interceptor that loses downloads.

Use existing repository conventions, locked dependencies and test infrastructure. Inspect `AGENTS.md` if present, current diff and build instructions. Preserve unrelated modifications. No publishing to browser stores, installation on personal browser profiles or machine-wide registry changes is implied by local coding. Use synthetic fixtures/development registration for verification; a user-requested installer run is a separate concrete deployment action.

Suggested commit boundaries follow work packages below; they are dependency boundaries, not mandatory branch names. Do not include generated bin/obj, real profile data, test cookies or machine-specific installed paths in source control.

## 2. Work package 0 — establish invariants and executable design spikes

Read these files together before edits:

- `src/AriaGui.App/App.xaml.cs`, `Services/AppDataPaths.cs`, `Services/Win32TrayIcon.cs` and current preference/viewmodel files.
- `src/AriaGui.Application/Downloads/DownloadCoordinator.cs`, `DownloadStorageContracts.cs`, `IAria2Client.cs`, `UserPreferences.cs`.
- `src/AriaGui.Infrastructure/Persistence/SqliteDownloadRepository.cs`, `.Browsing.cs`, `DownloadDbContext.cs`, `DatabaseSchemaUpgrade.cs` and migrations.
- `src/AriaGui.Infrastructure/Aria2/SingleInstanceLease.cs`, `Aria2ProcessManager.cs`, `ManagedAria2Client.cs`, `Aria2RpcClient.cs`, private-runtime and ownership helpers.
- `src/AriaGui.Infrastructure/Downloads/FileDownloadDestination.cs` and existing recovery tests.
- `scripts/Verify.ps1`, `Package.ps1`, `Test-Package.ps1`, release docs and current unpackaged publishing instructions.

Record baseline test/build status, target browser versions and packaging support. Write a short implementation ADR if a proposal below changes. Resolve these spikes before claiming the whole feature ready:

1. A binary native-host echo/hello harness launched by a throwaway extension in a synthetic browser profile. Prove framing, no console contamination and correct IDs/manifests.
2. A private pipe proof with same-user success, cross-user/remote denial, competing server detection and packaged/unpackaged endpoint isolation.
3. A coordinator/repository design proof that receipt+intent commit atomically while existing uncertain-engine recovery remains intact.
4. Windows attachment marking proof for browser-origin downloaded files; determine where completion handling can apply it without blocking download synchronization.

The credential/session spike may be documented now but is not a dependency for a no-credentials v1. No experimental secret forwarding should reach production behind an undocumented flag.

Exit criteria: decisions have testable implementation paths; no invented assumption about existing job IDs, APIs or single-instance activation.

## 3. Work package 1 — shared contracts and parsers

Create proposed `AriaGui.Browser.Contracts` project with no WinUI dependency. Define versioned envelopes, public GET request, handshake/status responses and enumerated error/result/job-intake states. Keep transport data separate from `EngineDownloadRequest`: external clients do not get an engine option surface.

Implement strict bounded deserialization. Validate UTF-8 byte length before JSON parsing, depth, duplicate/conflicting fields as appropriate to the selected parser, UUIDs, actions, URL controls/user-info/schemes and basename constraints. Use explicit JSON names and serialized fixture files. Maintain an allowlist for optional additive fields rather than accepting an unbounded property bag.

Implement independent frame readers/writers with exact-read loops, maximum size checks before allocation, deadlines and serialized output. Host/browser and pipe framing use their documented byte-order conventions. On malformed frames, return a safe error only when correlation is trustworthy; otherwise close. Errors must not include raw input.

Create a lightweight test project or locate tests in the existing layer convention. Test C01–C05, including lengths based on UTF-8 bytes rather than .NET/JS character counts. Shared JSON fixtures must round-trip through both C# and extension tooling. Do not depend on browser availability for these deterministic tests.

Exit criteria: a documented v1 schema with fixture-backed compatibility tests and no external process/network side effects.

## 4. Work package 2 — durable intake and stable job receipts

This is the highest-risk application change. The current `DownloadCoordinator.AddAsync` generates IDs and stores intent but returns no receipt; bolting a host request map around it after completion is incorrect.

Introduce proposed application contracts such as `ExternalDownloadRequest`, `ExternalDownloadReceipt`, `IExternalRequestRepository` and `ExternalDownloadIntakeService`. Names may follow repository style, but responsibilities must remain clear. Keep storage implementation in the same SQLite database. Add `ExternalRequestRow`, composite unique key, payload digest, job association, result state and timestamps with an EF migration and snapshot update.

Refactor coordinator internals so manual and external adds share URL/destination validation, ID generation, intent construction and engine-dispatch/reconciliation. The external transaction inserts request ledger plus job/attempt/file/pending operation atomically. Resolve duplicate-key races by loading the existing receipt and comparing digest. Preserve destination uniqueness. Do not hold coordinator locks during network I/O, and do not create a second synchronization loop.

Define an accepted receipt boundary immediately after durable commit. Engine dispatch is app-lifetime work; transport cancellation after that boundary cannot discard the job. Before commit, cancellation can stop validation. Add startup recovery for accepted-but-undispatched intents by using saved engine IDs and the existing uncertainty rules. Identify precisely how current recovery distinguishes never-sent intent from ambiguous send; do not introduce an automatic replay shortcut.

Preserve manual UI behavior through a wrapper that can await the dispatch result while browser intake receives a stable receipt. Do not expose internal engine IDs unless a test helper needs them. The extension only needs the app job ID. Add status query scoped to caller+installation, and tombstone handling when history is deleted.

Preference resolution belongs to an initialized application service. Reuse `DefaultFolder` semantics and existing fallback Downloads/AriaGUI path. The app must never accept a request while preference initialization failed or before the authoritative folder is resolved. A user changing the default folder after acceptance affects future requests only; persist the accepted destination with the job.

Migration checks: old databases upgrade with backup strategy intact; pending operations and existing history survive; downgrade behavior is explicit; database errors never produce accepted receipts. Verify privacy implications of full source URL persistence and ensure diagnostics/export redaction before browser intake release.

Tests: D01–D07 and W05 using real SQLite, injected engine faults and crash boundaries. Continue existing DurableDownload, QueueCommand, RecoveryLifecycle, Upgrade and destination tests. Add tests for actual transaction invariants, not merely DTO property copies.

Exit criteria: all repeated IDs resolve to one durable job, including concurrent calls, lost reply and app restart; manual Add behavior remains intact.

## 5. Work package 3 — GUI pipe service and lifecycle

Create proposed `BrowserBridgeServer` as an async hosted/lifecycle service composed in `App.xaml.cs`. It calls intake/status interfaces, never directly RPC. Use a per-installation/user endpoint and explicit local security. Keep pipe receive/parse work off UI dispatcher; use existing `IUiDispatcher` only for safe UI updates.

Introduce readiness coordination for preferences + repository + intake. The existing engine can be starting/offline after durable acceptance; define the queued state honestly. Do not return ready if database initialization failed. Register listener only after the primary singleton lease is held. Existing secondary app instances must not create a pipe.

Keep `SingleInstanceLease` unchanged in purpose for v1. Add a fixed launch mode only if needed for host wakeup; it carries no request payload. Ensure tray-hidden instance can receive work without forcing focus for every add. Context-menu success may show an app notification/status; avoid opening a new main window or interrupting typing on every background request. Visible errors should remain discoverable in extension popup and app history.

Implement shutdown admission barrier before `_lifetime.Cancel()` causes services to unwind. Track accepted processing tasks independently from pipe connections. Bound drain and rely on durable recovery when process exits; the existing checkpoint/engine ownership rules remain authoritative. Dispose listener handles predictably so update/uninstall is possible.

Test W01–W05 with real processes and synthetic runtime directories. Demonstrate simultaneous launch does not create a second aria2 owner. Confirm no deadlock involving UI thread, `_gate`, readiness task and shutdown. Document endpoint naming/discovery contract so host and app do not derive different paths.

Exit criteria: process-level hello/add/query works with GUI closed/running/tray/closing, normal user permissions, and denied cross-user access.

## 6. Work package 4 — native host

Create `AriaGui.NativeHost` as a small console/stdio executable. Open standard streams as binary; suppress banners and ordinary stdout logging. Safe stderr diagnostics contain codes/IDs only. The host validates the browser-supplied origin/extension arguments against the installation's generated allowlist; Firefox and Chromium invocation adapters are separate and tested.

Firefox supplies native-manifest path then add-on ID; verify both against the selected installation. Chromium supplies the extension origin and on Windows a `--parent-window=` argument (possibly zero from a worker). Never infer caller identity from a JSON field, and never treat a parent-window handle as authentication. [Firefox invocation contract](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Native_messaging), [Chromium invocation contract](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)

Host responsibilities in order: read bounded frame; validate contract; select a trusted installed app descriptor; connect pipe; launch selected GUI once if unavailable and allowed; wait within cold-start deadline; relay and return app response. Host owns no durable job queue and never sends accepted before app evidence. It must distinguish failure before forwarding from loss after forwarding so the extension sees unavailable versus unknown outcome.

Use a fixed executable path from installer-owned configuration. Pass arguments with process argument APIs, not shell-concatenated command strings. Do not run PowerShell/cmd for normal host operation. Never launch a path, pipe, URI handler or command provided by extension JSON. Validate installation descriptor permissions and target existence. Handle stale/moved portable installations with repair status.

Use `connectNative` for a bounded session while handling a user action/status reconciliation; close when idle. Avoid spawning one process per status poll where a single port suffices. Reconnect after disconnect with capped retry, not an infinite native-port keepalive. Browser worker termination and native host exit are normal failure cases, not reasons to delete jobs.

Publish host self-contained for supported x64 Windows initially. Check required .NET/native dependencies on a clean machine. AOT or single-file publishing is optional only after framing/startup/antivirus compatibility tests. Package metadata and notices must include this binary and dependencies.

Tests: C01–C05 through process stdin/stdout; W02–W04; host timeout/noisy-stderr/EOF fixtures; malformed caller arguments; incompatible app version. Verify not one byte of source URL/cookie fixture escapes stdout except the required request path (the host itself should not echo requests).

Exit criteria: a browser-launched host reliably relays to the selected app and fails safely when registration, startup or pipe identity is wrong.

## 7. Work package 5 — extension MVP

Use one shared source tree with typed contracts, browser adapter, background orchestration/state reducer, context menu, popup/options and build-generated manifests. Pick a minimal build toolchain already familiar to the repository owner; avoid a large UI framework for a small popup. Lock dependencies and record package licensing/notices.

Initial permissions: `nativeMessaging`, `contextMenus`, `storage`; add only what a concrete UI feature requires. No `cookies`, `<all_urls>`, `webRequest`, `downloads`, content scripts or externally-connectable webpage API in the context-menu-only release. The menu operates on `linkUrl`; non-HTTP(S) links are disabled/rejected. Sending public GET does not require a broad browser network permission because the app performs the request.

Register event listeners synchronously. Persist a generated per-installation UUID and unresolved request ID/timestamp/state without raw URLs. A fresh user gesture allocates an ID; retry/reconnect preserves it. On worker wake, query unresolved IDs. Deduplicate repeated callbacks locally and let the app ledger remain authoritative across crashes.

Popup states: not connected/install help, connecting, ready, sending, added with job ID, rejected with safe actionable reason, and cannot confirm. Display “Added to AriaGUI” only after durable accepted result. Do not imply transfer completion or resume support. Provide Open AriaGUI through a narrowly scoped host action only if implemented and authorized in protocol; otherwise use normal app navigation guidance, not an undocumented action.

Options expose integration enablement and connection test/version information. Default automatic mode absent/disabled. Clearly state that public link downloads are GET, app history retains URLs, and browser-only export/authentication may need to stay in the browser. No raw payloads in console logs. In private/incognito contexts, default to refusing handoff until a dedicated privacy policy is implemented.

Generate separate Chromium and Firefox manifests/native manifests. Keep extension/store identities explicit in build configuration; development IDs use development host registration. Verify Firefox background support and minimum versions from current official docs rather than copying Chromium `service_worker` blindly. References: [Firefox background](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/background), [Chrome lifecycle](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle).

Tests: shared schema fixtures, state reducer termination/reconnection cases, E01–E07 with actual browser versions in synthetic profiles. Browser automation may support portions of this, but record manual extension/native-host acceptance where automation cannot exercise browser UI. No personal cookies/profile inspection.

Exit criteria: a user can right-click a public file link and see one correctly saved app job in Chrome, Edge and then Firefox; unavailability never cancels browser work.

## 8. Work package 6 — installation, settings and release integration

Select a per-user installer approach based on existing release goals. Do not convert all current MSIX tooling incidentally. Add host/extension artifacts and a development registration script with explicit browser/channel/install-root parameters, dry-run/readback and uninstall/repair. Use native registry APIs or PowerShell registry cmdlets; no opaque `.reg` file with a developer machine's absolute paths.

Generate Chrome/Edge `allowed_origins` manifests and Firefox `allowed_extensions` manifests with stable installed host path and host name. Register exact HKCU native-messaging keys for each browser. Development registration must not replace production. Capture old values for rollback and remove only values still pointing to this installation at uninstall. Test paths with spaces and non-ASCII characters. [Edge native host installation](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging)

Add app preferences for integration enablement/selected installation status through the existing JSON preference model, with defaults and backward-compatible deserialization. Do not store browser secrets or raw pending URLs in preferences. Settings show a connection diagnostic that distinguishes extension missing, host missing, version mismatch and app not ready. Avoid “connected” based solely on an installed registry value; perform hello.

For unpackaged deployment, stable installation directory and app launch path are straightforward acceptance requirements. For MSIX, complete a separate proof of host discoverability and stable launch across package version replacement; document whether a companion per-user installer is necessary. Do not write to protected package directories or assume package virtual registry entries are visible to browser discovery. Keep browser integration support matrix honest until tested.

Update package integrity/notice scripts and release acceptance documents. Include extension ZIPs, native manifests/templates, host binary, hashes and installation instructions in an appropriate release artifact directory; do not silently install store extensions. Signing/store submission remains an explicit release task.

Tests: I01–I04. Run clean-machine installation without developer .NET/WinAppSDK assumptions, update while app/host are active, rollback failed host handshake, uninstall with other channels installed, and reinstall retaining history.

Exit criteria: a reproducible installation/repair/uninstall path and evidence for every advertised package/browser combination.

## 9. Work package 7 — release verification and handover

Run the repository's required verification plus targeted new unit/integration/process tests. Re-run broader checks only when changes justify them. Validate one real public-file flow using a controlled server, SHA-256 equality and selected default directory. Check existing manual adds, queue limits, pause/resume/retry, tray lifetime and recovery to catch regressions from coordinator changes.

Exercise N01–N06 and document the actual resume behavior. The browser integration must not be marketed as fixing origins that ignore Range, change content, expire signed URLs or require browser-only authentication. If ETag changes cannot be handled safely, record that limitation and block unsafe recovery claims.

Inspect diagnostics/session/history export using synthetic markers. Verify attachment marking and no automatic file opening. Inspect extension permissions in the installed browser, not just source manifest. Confirm release host manifest IDs are correct and do not include development IDs.

Produce a short implementation report: changed files, schema migration, supported browsers/versions/packages, test results, known limitations and deferred phases. Link to exact local artifacts. Do not claim an extension is published or installed for the user unless that occurred under separate authorization.

Release gate: all v1 C/D/W/E/P/I requirements pass; N limitations are explicit and safe; no cookie/auto/protocol capability is advertised. A known duplicate/lost-job failure in v1 blocks release.

## 10. Later phase A — best-effort automatic takeover

Begin only after v1 is stable and its residual risks are accepted. Add optional downloads permission and explicit opt-in UX. Establish replayability eligibility; `onCreated` URL alone does not expose method/body/auth and is insufficient. Start with controlled/allowlisted sources and user-visible experimental status. Do not infer that every `.zip` URL is safe to replay.

Implement prepared reservation/commit/abort/status under the app ledger as specified in the protocol document. Before commit there must be no origin request or active aria2 transfer. Handle browser completion, pause failure, cancellation ambiguity, changed URL/name, extension-origin downloads and worker restart. Persist only the minimum browser item identity/state needed for recovery.

Run A01–A04 and crash injection at every boundary. Add UI for canceled-browser/unconfirmed-app state. Default to preserving browser history. Never advertise atomic fail-open, never restart browser automatically after an unknown app commit, and never import/delete browser partial files.

Exit gate is a separate release decision: recorded loss/duplication residuals, truthful UX and tested recovery. If requirements demand mathematically atomic loss-free takeover, this architecture cannot provide it with these browser APIs; retain explicit context-menu mode.

## 11. Later phase B — credentials and richer HTTP context

Run S01/S02 before adding permissions or DTO fields. Inspect the exact bundled engine's session serialization and redirect header forwarding with synthetic credentials. Use all normal/suspend/recovery/forced-crash paths because periodic `save-session` operates independently of UI choices. App-only encryption does not protect secrets serialized by aria2.

Only after a feasible no-unapproved-persistence and redirect-scope strategy exists, introduce typed request context with cookie store/partition evidence, allowlisted user-agent/referrer handling where necessary, explicit origin grants and credential expiry. Header validation is repeated in app infrastructure. Recovery must surface expired/lost credentials rather than retrying unauthenticated indefinitely.

Keep POST bodies, browser response streams, client certificates, integrated authentication and private/container mixing unsupported unless separately designed. Add synthetic tests for secure/path/domain matching, multiple same-name cookies, partition mismatch, redirect origin changes and downgrade. Do not send whole cookie stores or expose an arbitrary header editor by default.

## 12. Later phase C — protocol activation and AppInstance migration

Implement only if users need OS/web links independent of the extension. First support `ariagui://open`. URL add requests must open a confirmation, reject secrets/options/path parameters and be bounded/rate-limited. They are untrusted input.

If replacing the current GUI lease for activation routing, use a custom entry point and Windows App SDK `AppInstance` redirection according to current docs; remove conflicting GUI ownership logic only after behavioral equivalence tests. Preserve separate aria2 owner/session safeguards. Handle redirected activation via dispatcher/readiness queue; do not create a second DI container or main window. [Windows single-instance design](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance), [protocol registration by package model](https://learn.microsoft.com/en-us/windows/apps/develop/launch/handle-uri-activation).

Test protocol invocation from browser, shell and malicious malformed input; cold/warm/tray/closing activation; both installation models; URL quoting/encoding; multiple rapid launches and focus behavior. No cookie-bearing payload on command lines.

## 13. Teaching notes for a Windows application newcomer

**Extension versus host:** extension JavaScript is sandboxed in the browser. Native messaging allows a browser-approved small program to communicate over its stdin/stdout. That program is a separate process; it does not automatically become the existing GUI. The pipe is how it reaches the running GUI.

**Named pipe versus RPC:** the pipe is private local IPC owned by AriaGUI. aria2 RPC is a separate authenticated HTTP service owned by the engine manager. Keeping them separate prevents the extension from bypassing job history, destination policy and recovery.

**Instance versus engine ownership:** only one GUI should own a runtime database, and only the correct engine process should be managed. The existing file lease helps exclude another GUI; the engine has its own ownership/recovery checks. Replacing one with an activation API does not remove the other requirement.

**Durability versus network success:** a database transaction can reliably remember “this request is our responsibility.” It cannot atomically include a browser cancel or HTTP RPC to another process. A stable request ID and reconciliation let the program find out what happened after a timeout. Timeout means uncertain, not rejected.

**UI thread:** WinUI controls belong to their dispatcher thread. Pipe reads and database/network waits must not block it. Application services can run asynchronously; only visual updates are dispatched. App startup must expose readiness explicitly because creating a window does not prove preferences/database/engine are ready.

**Installation versus copying an EXE:** a browser finds a native host through registered manifests with exact extension IDs. A copied executable lacks that wiring. Updates and uninstall must maintain the registration safely, and packaged paths differ from ordinary folders.

**Resume versus retry:** retrying a URL starts another HTTP operation. True resume uses compatible retained partial data and a server that supports appropriate byte ranges with a stable representation. Browser partial data is not automatically aria2 partial data. Test the request/response and final hash, not just the progress display.

**Secrets versus ordinary strings:** URLs can contain credentials even without cookies. Logs, histories, database backups, process arguments and engine sessions are all possible storage. Treat a cookie feature as an end-to-end data-lifecycle change rather than a convenient extra JSON field.
