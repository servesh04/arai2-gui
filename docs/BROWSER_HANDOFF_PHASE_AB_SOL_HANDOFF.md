# Phase A/B implementation handoff for GPT-6.1 Sol

Baseline: `2753957` plus uncommitted Phase B feasibility files. Read [review and evidence](BROWSER_HANDOFF_PHASE_AB_REVIEW.md), the canonical architecture/plan/protocol and both WP8 and Phase B evidence before editing. This handoff defines future work; this review does not enable production features or authorize Phase C.

## Target decision and invariants

Preserve a single app database and existing coordinator/dispatch/GID recovery path. Automatic takeover remains an optional best-effort feature with a real cancellation-to-commit loss window. If that residual risk is unacceptable, finish with explicit context-menu mode and document A as deferred. B remains disabled unless a new transport can meet both persistence and redirect gates. C is demand-driven and independently deferred.

Required invariants:

1. Prepared means destination and durable intent only: no origin probe, engine Add, or queued transfer.
2. Commit/abort/expiry serialize in SQLite. Exactly one accepted intent, stable request key and job ID; retries never extend expiry or allocate a second GID.
3. Accepted intent survives opt-out, client disconnect and lost ACK. Shutdown stops admission and preserves existing dispatch/recovery ordering.
4. Browser cancellation is outside the transaction. Preserve history and partial files; no erase, blind fallback or automatic restart of an unknown outcome.
5. Public exact-source URLs and credentials have explicitly different retention policies. Cookies/Authorization never enter durable intent, gateway route/journal/representation, engine options/session, or diagnostic text.

## Package A0: close domain lifecycle and status

Work in `PreparedTakeoverService`, `PreparedTakeoverContracts`, `SqliteDownloadRepository.Takeover`, `DownloadDbContext` and a new migration if needed. Keep the service unregistered and v1 unchanged.

* Add a single repository cleanup operation used on startup and periodically under app lifetime. Indexed bounded batches release expired reservations and clear the live payload. A commit still checks its own row's expiry under the transaction, regardless of cleanup progress.
* Specify wall-clock adjustment policy. A 60-second UTC deadline can lengthen under clock rollback; either conservatively invalidate on observed rollback/restart or clearly document/test the wall-clock guarantee. Do not pretend a process-local monotonic timer alone survives restart.
* Bound active reservations per caller/installation and globally inside the transaction. Return a retryable busy result before creating a row. Avoid scanning the entire historical ledger on every status call.
* Version the serialized prepared intent, or explicitly version/reject its persisted schema. Validate persisted identity associations before commit. Avoid expanding into a generic saga framework.
* Separate query/transport outcomes from durable state in any new public contract. Define committed-with-history-deleted behavior and cross-action request-key conflicts. Fix duplicate metadata only if exposing it.
* Keep destination DB protection and repeat filesystem admission. Test a file/control file appearing after prepare, directory replacement, reserved destination contention and changes to default folder. Let existing publication protections remain the last authority for file writes.
* State source retention accurately and run synthetic signed-URL scans through DB/WAL/backup boundaries. Decide what retained source storage is approved; no false physical-erasure guarantee.

Done when focused reservation/intake/upgrade tests pass, idle cleanup is measured without other calls, bounded-load behavior is established, old databases migrate safely, and status cannot cause a duplicate or resurrect a tombstoned request. Record actual abrupt child-process termination in addition to existing exception hooks.

## Package A1: specify eligibility and browser state before adding cancellation

Create a browser-independent reducer with explicit steps and browser-specific adapters. Persist only request/install/profile/item/start identity, phase, timestamps and stable accepted receipt; no source URL or headers in browser local storage. Write the pending record **before every first irreversible browser side effect**, including before pause if pausing is supported.

Use these conceptual states (names may differ):

| State | Allowed next action | Recovery after worker loss |
|---|---|---|
| observed | Validate controlled replayable source and consent | Drop if no side effect occurred |
| preparing | Query/prepare same key | Query same key; inspect browser item |
| prepared | Re-read item; cancel only if still eligible | Re-read before any cancellation |
| cancel-requested | Re-read cancellation outcome | Never assume cancel succeeded |
| cancelled-unconfirmed | Commit/query same key | Status first; idempotent commit only with valid evidence |
| accepted | Display stable job receipt | Query same key; no new mutation |
| aborted/expired | Resume paused browser where possible or offer deliberate recovery | Never auto-create replacement |
| cannot-confirm | Display persistent recovery guidance | Bounded status retries, explicit user action if terminal/unreachable |

Resolve replayability by a controlled integration/source contract, not URL extensions or a caller-supplied GET flag. Bind approval to exact resource, profile/item/start, allowed request context, expiry and policy revision. Never assume cookies/auth are absent merely because a URL has no embedded password. Reject unsupported contexts before pausing. Include initiating extension, private mode, browser danger/block state and completion state in adapter eligibility.

The adapter must translate each browser's fresh item state/error to domain cancellation only after its own cancellation attempt and identity checks. Treat disappearance, unrelated interruption and ambiguous completion as insufficient. A browser does not expose a transactional cancellation token; retain the documented residual uncertainty.

Recovery may re-read the source/name from the same browser item to reconstruct a same-key commit. If unavailable or changed, stop with truthful recovery UI. Alternatively design an app-held prepared reference with identity-bound commit, but review that protocol explicitly; do not silently persist source URLs in the extension to solve the problem.

Done when reducer/adapter fixture tests cover every state transition, no side effect precedes its recovery record, profile isolation is demonstrable, and an actual controlled browser test proves eligibility can be established. If this proof is unavailable, leave automatic takeover disabled and stop A expansion.

## Package A2: versioned transport and real acceptance

Only after A0/A1, introduce a separately advertised takeover capability/actions in contracts, native relay and app dispatcher. Keep v1 public GET backward compatible. Reuse framing, caller/installation scoping, readiness, queue limits and safe errors. Never trust JSON flags as browser proof; the extension adapter is part of the trusted product within the existing same-user threat model.

Add optional downloads permission and opt-in UI with accurate experimental wording. Do not request cookies or broad host access for A. The UI must visibly distinguish browser complete, prepared, accepted, and browser canceled/app cannot confirm. Keep pending recovery across restart, permission revocation and app unavailability.

Run A01–A04 against every claimed browser with exact build/hash/version evidence. Inject worker/native-host/app death before and after pause, prepare persistence, cancellation request, cancellation observation, commit and ACK; include expiry while disconnected and app shutdown. Count jobs/GIDs and origin requests, verify browser files/history survive, and test another extension's download and deliberate fallback without loops. Record loss/duplication residuals explicitly. Domain tests cannot replace this package.

## Package B0: close current negative feasibility evidence

Preserve the current spike with no production contract expansion. State `S01 blocked; S02 blocked; direct aria2 headers rejected`. Strengthen automatic checks for all documented lifecycle fields and each redirect case if maintaining the harness as regression evidence. Record that its zero exit means the experiment completed, not that credentials are safe. Keep synthetic artifacts private and do not copy raw markers into review text.

This is a legitimate terminal result for the current Phase B investigation. It does not require building a replacement credential subsystem to claim the investigation is finished.

## Optional Package B1: bounded in-memory transport experiment

Proceed only as a separately agreed feasibility package. Reuse `PublicHttpOriginTransport` connection pinning, `HttpContinuityRelay` representation validation, and existing staging/publication behavior. Avoid a second downloader, separate credential aria2 process or general proxy service in the first experiment.

Proposed split:

* Durable transfer record: opaque job/attempt/context-required marker and ordinary approved source history policy. No credentials. If source query itself is a credential, explicitly exclude that case initially or design its separate retention policy.
* Volatile credential lease: owned by app lifetime, exact HTTPS origin/resource and job/attempt/profile context, expiry/revocation token, typed allowlisted fields. No serialization, debugger/log formatting or generic dictionary exported to callers.
* Origin transport: checks lease before each request, owns header attachment, disables automatic redirects and cookies. First slice refuses every redirect. It cancels an active body at lease expiry/revocation and reports a stable safe reason. It never falls back to a direct engine URL or an unauthenticated request.
* Loopback gateway: receives only engine-facing opaque capability requests and validated range; ignores arbitrary engine-supplied origin headers. A durable capability is not credential authorization. On restart, credential-required routes remain unavailable until a deliberate grant is reacquired.
* Recovery/UI: mark credentials unavailable/expired; preserve safe partial files and validators. A new grant cannot silently authorize a changed source or representation. Resume still requires the existing strong entity-continuity checks.

Start with a synthetic non-browser grant and one exact origin. Do not add browser cookie permissions until S01/S02 pass through the experiment. Cookie store/partition/container/SameSite and same-name selection need their own browser-specific design; unsupported contexts remain rejected. If later allowing redirects, choose each next request's credentials afresh from exact authorized context, reject downgrade, and prove every hop. Never forward an existing header list to the next URI.

Tests must cover normal/periodic checkpoints, pause/resume, graceful stop, abrupt kill, restart, expired grant during headers/body, revoked permission, old engine session/capability, changed entity and real diagnostics export. Scan DB/WAL/backups/config/engine sessions/routes/transfer journals/representations/logs/export for markers. Persisted artifacts must not reauthorize a credential operation after restart. Specify OS paging/crash-dump exclusions honestly; “memory only” is not universal physical secrecy.

Before calling the gateway reusable, address terminal route retirement and capacity, plus startup admission ordering. Retain compact durable publication/idempotency evidence while disposing/revoking active route resources. Test delayed requests and restart races. Keep Kestrel logs disabled and use structured safe diagnostics.

Done only when both gates pass under the chosen documented scope. Success on one controlled origin does not certify general browser cookie export.

## Phase C gate

Do not blend C into these changes. After A is either accepted or explicitly deferred and B is closed as blocked or separately accepted, establish actual demand for OS activation. The first C package should be `ariagui://open` only, with its own cold/warm/tray/shutdown/install tests. Add-URL confirmation and AppInstance migration are separate decisions. Retain current GUI lease until tested replacement has one clear owner; preserve aria2 ownership independently.

## Required document patch list

Use the six concrete canonical alignment items at the end of the review. Every package report must state implemented, disabled, blocked, untested and release-accepted separately, list exact commands/artifacts, and avoid representing mocked/browser-free tests as A01–A04 acceptance. Do not rewrite old evidence as if later checks happened at the old baseline.

## Review-time verification

The reviewer attempted the focused existing-binary command `dotnet test tests/AriaGui.Infrastructure.Tests/AriaGui.Infrastructure.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~PreparedTakeoverTests --verbosity minimal`. VSTest aborted after failing to connect to the testhost within 90 seconds, with no assertion results. No test pass is claimed. This command is a focused domain check only; it cannot validate current source compilation or browser/credential release behavior. `git diff --check` passed; historical WP8 results remain independently labeled historical.
