# Phase A/B architecture and implementation review

Reviewed 2026-10-13 against committed Package 8 `2753957` and the uncommitted Phase B spike. This is a review, not authorization to enable a feature. Companion: [implementable Sol handoff](BROWSER_HANDOFF_PHASE_AB_SOL_HANDOFF.md).

## Decision

Keep automatic takeover and credentials disabled. Phase A has a useful transaction foundation, but no browser adapter or release acceptance. Phase B has successfully established that direct headers on the shared persistent aria2 engine are infeasible under the stated privacy and redirect requirements; S01/S02 remain blocked, not passed. Close B as a **documented blocked feasibility investigation**, or separately authorize a bounded transport experiment. Do not call it a completed credential feature.

Do not start Phase C production work merely because A/B have code or evidence files. First close this review's safety and status decisions and retain the current v1 release gates. C can be independently deferred; making B fully functional is not a technical prerequisite for `ariagui://open`. AppInstance migration adds lifecycle risk to the same acceptance/recovery machinery and should not overlap unresolved A work.

## What is sound

* `PreparedTakeoverService` is absent from App composition and the v1 wire schema; the extension has no cancellation adapter. Trusted policy plus saved consent and an exact URL grant are required by the domain service.
* Prepare creates no Jobs/Attempts/Files/Operations rows or engine request. Commit removes the reservation and adds the existing intent rows in one SQLite transaction. Independent writers enter a write transaction before checking expiry. Abort cannot revoke a durable commit.
* Destination uniqueness exists in SQLite and triggers protect Jobs insert/update from reservation conflicts. Commit repeats filesystem validation against the reserved destination. This is a database reservation, not an exclusive reservation against arbitrary filesystem writers; downstream no-overwrite/publication protections remain necessary.
* Existing accepted work uses app-lifetime dispatch and saved GIDs. A lost browser/native connection does not authorize a second Add or remove an accepted job.
* The credential spike pins the engine hash/version, uses synthetic markers, exercises periodic save and restore, and records route-correlated redirects. The documentation distinguishes negative feasibility evidence from feature acceptance.

## Prioritized findings

Priorities describe work required before activation. A release blocker in a disabled path is not evidence that the current public-GET feature leaks credentials.

### P1 — No browser-level proof closes the cancellation/replayability boundary

**Observed:** `src/AriaGui.Application/Downloads/PreparedTakeoverContracts.cs` defines `ReplayablePublicGetGrant` as only an exact URL and `BrowserItemEvidence` as caller-provided domain facts. `PreparedTakeoverService.cs:39-59` accepts a `Cancelled` phase after identity checks. Grants have no issuer/context/expiry. The tests manufacture cancellation; no browser API is called. `BROWSER_HANDOFF_WP8_EVIDENCE.md` accurately defers A01–A04 browser acceptance.

**Consequence:** URL membership cannot establish that a particular browser download was a public repeatable GET. The same URL may participate in a POST or one-time response. A fulfilled cancellation call does not establish the required outcome. Chrome exposes an interrupted state and reason, and cancellation can race other outcomes; the adapter must interpret fresh observations, not map promise fulfillment to `Cancelled`. [Chrome downloads API](https://developer.chrome.com/docs/extensions/reference/api/downloads).

**Action:** Design and test the trusted adapter before exposing DTOs. Restrict the first experiment to a controlled source whose GET/replayability semantics are established independently; a generic `onCreated` listener cannot manufacture this guarantee. Bind grant, caller/profile, item/start time, exact source and lifetime. Persist a recovery step before pause/cancel. Read the item again after cancellation; require the browser-specific canceled outcome, unchanged identity, and no completed result. Missing/ambiguous evidence must remain unresolved or abort where safe. Do not capture another extension's download or a dangerous/blocked browser item.

**Acceptance:** Real throwaway Chrome/Edge/Firefox profiles, A01–A04, worker death before/after each persistence/API/message boundary, duplicate events, changed item metadata and cancellation/completion races. Record real versions. An internal enum test is insufficient.

### P1 — Expiry currently means logical row expiry, not 60-second secret erasure

**Observed:** `SqliteDownloadRepository.Takeover.cs:15-28` expires rows only on takeover lookup/mutation and selected intake writes, then nulls serialized intent. The repository enables WAL (`SqliteDownloadRepository.cs:20`); `DatabaseSchemaUpgrade.cs:26-34` explicitly backs up committed WAL data. No secure-erasure policy or retained-artifact marker test is present. The WP8 evidence already acknowledges idle records can retain intent.

**Inference requiring verification:** Nulling `PreparedIntentJson` does not establish removal of old bytes from WAL, database free pages or preexisting backups. A signed query URL may therefore outlive logical expiry even after cleanup. This is not a measured marker leak from the current review.

**Action:** Narrow the claim to “the live reservation payload is cleared on logical cleanup.” Define approved private source-URL retention separately from credentials. Add startup plus periodic bounded cleanup before exposing takeover; it must not depend on another download arriving. Do not promise physical erasure on commodity storage. If source persistence for aborted prepares is unacceptable, reconsider durable raw-source storage and recovery design before activation. Cookies/tokens must not be added to this JSON.

**Acceptance:** Idle expiry with no incoming calls, restart after expiry, DB/WAL/migration-backup synthetic URL inspection, clock rollback/forward and sleep/wake behavior. Document the actual retention policy and physical-erasure limits.

### P1 — Direct engine credential forwarding fails both required gates

**Observed:** `tests/AriaGui.Browser.Spikes/CredentialSpike.cs:40-137`, the Phase B evidence document, and `artifacts/browser-phase-b/verified/evidence.json` establish session persistence and cross-origin transmission. All five marker fields persist in the recorded session stages, and the recorded port/hostname/downgrade targets receive all five. The wrapper independently requires five markers in the session. The spike intentionally disables TLS validation only for synthetic TLS.

**Action:** Keep production contracts/RPC options/permissions unchanged. Do not fix this by deleting session files after saving, encrypting SQLite, or attempting to scrub engine options after acceptance. None prevents the observed earlier serialization or redirect transmission. Any alternative must prevent origin credentials from entering the persistent engine at all, then rerun S01/S02 through its actual lifecycle. Preserve the spike as negative evidence and label its zero exit status clearly.

**Evidence limitation:** The spike is not a full app suspend/recovery/WAL/backup acceptance suite. Its aggregate assertions require cookie persistence and some wrong-origin cookie transmission, while the report contains the detailed five-field/per-case observations. Strengthen per-case assertions if future runs are used to enforce all table claims automatically; unexpected engine behavior should become an explicit inconclusive/changed result requiring review, not an inferred pass.

### P1 — The continuity gateway is a candidate reuse point, not an established credential boundary

**Observed:** `ContinuityLoopbackGateway.cs:19,45-52,80-103` persists route source and capability and restores them on startup. `ContinuityTransferLedger.cs:13-17` persists Source in transfer journals; `HttpRepresentationStore` persists effective resource. `PublicHttpOriginTransport.cs:29-35,88-121` currently sends no cookies, disables automatic redirects/proxy/cookie jar, pins approved connections and explicitly follows bounded redirects. `HttpContinuityRelay` validates representation continuity; it does not own credential grants.

**Consequence:** Adding a header field to Route/State/representation would recreate persistence exposure. A saved loopback URL is a durable capability today; it must never independently reauthorize credentials after app restart or grant expiry. The current positive public-network/continuity tests do not prove credential revocation, header scope or loss-of-secret recovery.

**Action:** Reuse validated connection selection, byte streaming and file publication only behind an explicit volatile credential lease. Keep credentials out of routes, journal, engine request, representation, logs and exception text. On restart, a credential-required route must be unavailable until a new deliberate authorization binds the same job/context; never silently retry without credentials. Start a feasibility slice with HTTPS, one exact origin, no redirects, no cookie mutations, and no private/container/partition support. Add broader behavior only with separate evidence. Enforce expiry during active streaming as well as before opening a request.

### P2 — Reservation admission and cleanup do unbounded work as the ledger grows

**Observed:** `ExpirePrepared` enumerates all expired prepared rows and does a reservation delete for each; every takeover lookup starts a write transaction and runs this sweep. `DownloadDbContext.cs:20-26` has no takeover phase/expiry index. Prepare has a payload cap but no active reservation quota.

**Consequence:** Automatic events or repeated unique IDs can increase write lock time, memory and reserved destinations. Status polling unnecessarily competes with acceptance, and a large expired backlog affects manual Add.

**Action:** Add indexed bounded expiration batches and caller/installation/global admission quotas in the same write transaction. Keep target-key expiry authoritative inside commit even if a background sweep has not reached it. Avoid a second queue/database. Measure worst-case latency with a realistically large terminal ledger and active reservations before choosing limits.

### P2 — Gateway terminal retention consumes its finite live-route capacity

**Observed:** `ContinuityLoopbackGateway.cs:91,102` counts `_routes` toward 4096; `CompleteRoute` only adds to `_completedRoutes`. Completed route files and origin transports remain. `ContinuityTransferLedger.LoadAsync` restores completion denial from its durable terminal journal, so completion denial is not simply forgotten on normal ledger startup. No route pruning path was found.

**Consequence:** Over enough attempts, including completed ones, new registration fails at capacity and startup retains historical transport objects. This matters independently of B, and becomes more important if a credential store is attached to routes.

**Action:** Define durable retirement after engine/session reconciliation, revoke active grants, dispose origin transports and remove live routes without losing idempotency/publication receipts. Test restart at each retirement boundary and delayed requests from an old engine session. Do not indiscriminately delete journals that protect completed publication.

### P2 — Diagnostics redaction cannot support arbitrary richer context

**Observed:** `scripts/Export-Diagnostics.ps1:33-42` scrubs URLs, RPC tokens and Authorization but does not enforce a structured safe event schema. `artifacts/browser-phase-b/verified/diagnostics-evidence.json` records custom-header/User-Agent marker retention in actual exports. Production does not enable these verbose synthetic engine logs, so this is not proof of current production credential logging.

**Action:** For B, emit only allowlisted structured diagnostic fields and exclude unknown raw transport/engine logs. Treat user-agent/referrer/custom context as potentially sensitive. Verify both current and rotated logs, exception paths and exported bundles using markers. More regexes alone cannot certify arbitrary-header secrecy.

## Smaller contract and maintenance issues

* `PreparedTakeoverService.Normalize` validates a copy with `Trigger = "linkContextMenu"` but returns the original request trigger. The digest omits trigger and the repository does not need it. Prefer a dedicated internal takeover input with no irrelevant explicit-handoff trigger; do not let an eventual wire adapter inherit this accidental ambiguity.
* `PreparedTakeoverPhase` mixes durable phases with transport/query outcomes (`Unknown`, `Unavailable`, `NotFound`, `Rejected`). Keep this internal for now; define a small discriminated wire result with one durable state and a separate outcome/error, rather than serializing the enum wholesale.
* Concurrent successful commit calls can all report `Duplicate: false` because commit returns only a record. Identity/job correctness is preserved; if duplicate semantics become public, return whether this transaction changed the row.
* A committed takeover can later contain an expired accepted receipt after history deletion. Specify terminal status explicitly so clients do not interpret outer `Committed` as a live job. Add history-delete/tombstone/reused-key tests across both intake action families.
* The comment in `HttpContinuityRelay.cs:26-27` says public composition refuses redirects, while `PublicHttpOriginTransport.OpenAsync` implements bounded redirects. Correct the comment; do not treat it as an enforced credential policy.
* The relay constructor's `exactEffectiveResource` is validated but not retained. Current public transport binds its source and later representation checks bind effective resource. Clarify that contract (initial source versus eventual effective URL) before introducing alternative credential transports; do not claim the unused parameter enforces equality.
* Prepared JSON serializes the complete `DownloadEntry`. This avoids a second job-construction path today, but couples reservation recovery to an evolving object graph. Add an explicit schema version/migration rule before activation; a compact versioned prepared-intent record is preferable if refactoring is needed, not a second generic workflow engine.

## Review verification and limits

Read the three canonical browser documents, WP8/B evidence, reservation service/repository/migration/tests, gateway/relay/origin/transfer ledger, credential harness/wrapper and diagnostics exporter. Inspected git status/diff and local diagnostics evidence. No production files or feature flags were changed, no real browser/profile was opened, and no new credential experiment was executed for this review. The focused existing-binary PreparedTakeover test run aborted because VSTest could not connect to its testhost within 90 seconds; no passing/failing assertions were reported. `git diff --check` passed. Existing evidence counts remain historical evidence, not new end-to-end validation.

## Canonical-document alignment required

1. Architecture opening: replace blanket “not implemented” with a dated status distinction: implemented v1/domain groundwork, disabled/unaccepted A, blocked B, deferred C. Link the implementation evidence and this review.
2. Plan section 10: label Package 8 as completed domain slice and list browser eligibility, durable adapter/recovery, expiry retention and A01–A04 as remaining gates.
3. Plan section 11: record direct-header strategy rejected by measured S01/S02. Any gateway experiment is a new bounded feasibility slice, not permission to add production fields.
4. Plan section 12: retain demand-driven scope; no default AppInstance migration and no start while acceptance/lifecycle ownership is ambiguous.
5. Protocol later-takeover section: specify browser-specific cancellation mapping, pre-side-effect durable state, monotonic recovery and source rehydration failure; do not expose the domain enum as the protocol design.
6. WP8 evidence: replace “source only while prepared” with “live serialized reservation source cleared on logical termination; retained SQLite/WAL/backup bytes are not certified erased.” Keep the idle-expiry limitation explicit until fixed.
