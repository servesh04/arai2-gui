# Browser handoff Package 2 evidence

2026-10-11. Base: `e3d0209`. This package adds application intake and persistence only. It does not register a host, expose a listener, install an extension, or enable browser integration.

## Decision and durable boundary

`ExternalDownloadIntakeService` uses the existing coordinator, destination policy, history database and engine interface. `ExternalRequests` has a composite trusted-caller/installation/request key and unique nullable job FK. Accepted receipt, job, attempt, file and pending-add operation commit in one SQLite SaveChanges transaction. Database constraints arbitrate concurrent writers; duplicate payloads converge and changed payloads conflict. Stable domain rejections are stored, including preference initialization failure. Corrected preferences require a new request ID after a stored rejection.

Acceptance means durable intent, not engine success. Work runs under application lifetime after commit, independently of client cancellation. Startup schedules accepted `NeverSent` intents without requiring a client connection. Dispatch waits for the existing lifecycle's verified Ready event before its durable CAS claim. Offline/startup status stays queued and unclaimed; shutdown while waiting leaves recoverable work. Once claimed, an unobserved outcome is uncertain and is never automatically added again. A crash between claim and actual engine call deliberately sacrifices automatic progress to avoid duplicate transfers. A readiness loss immediately after Ready has the same conservative ambiguity.

Unexpected dispatch failures expose a safe `LastDispatchFailure` enum (StorageUnavailable or NotReady), without raw exception/URL text. An unexpected pre-claim failure leaves durable queued work for explicit status/request retry or restart; there is no background retry timer. A deterministic readiness failure/restart test proves it is observable and remains safely dispatchable. This is an intentional bounded-retry limitation, not guaranteed autonomous progress after arbitrary component faults.

The domain receipt preserves existing job states, including Removed, and carries an uncertainty flag. It is not the wire DTO. A later transport adapter must map Removed/uncertainty and storage-read failures to the v1 schema; it must not serialize domain records directly. Status uses database state and is scoped to the same trusted caller and installation.

New requests read and validate authoritative persisted preferences before choosing the existing fallback folder. Failed reads do not use silently initialized UI defaults. Existing durable destinations do not change when preferences change. Terminal-history deletion goes through the coordinator facade so runtime checkpoints cannot resurrect deleted rows; receipt tombstones retain IDs for at least 90 days. Requests older than seven days are rejected after known-ledger lookup. Accepted records with jobs are never pruned.

Exact opaque query text survives contract parsing, SQLite persistence, reload, engine requests and RPC JSON. `Uri.AbsoluteUri` was observed decoding `%41%7e`; external entries therefore retain the original fragment-free source separately. Retry, explicit recovery and restricted session-file formatting use it. Manual URI behavior is unchanged. No actual origin request or aria2 internal request-target normalization was measured.

## Evidence matrix

| Gate | Executable evidence | Disposition / limits |
|---|---|---|
| D01 | 20 concurrent requests across two independent SQLite repositories/coordinators converge to one receipt/job/GID and one engine Add; lost-response retry and caller/install status isolation | Pass for in-process independent SQLite contexts; separate OS-process contention untested |
| D02 | Different deliberate IDs with different destinations create two jobs; same destination collision is durably rejected | Pass |
| D03 | Changed URL/name/timestamp and percent-escape case conflict; fragments normalize without query reordering | Pass |
| D04 | SQLite trigger abort rolls back all five row sets; before-commit cancellation; postcommit injected lost response; NeverSent restart; claim-before-send restart; app-lifetime cancellation and readiness recovery | Transaction/phase simulations pass; abrupt OS process kill and real power loss untested |
| D05 | Engine records Add then loses its reply; saved GID is observed after restart with no second Add | Pass with deterministic engine fake, not a real origin |
| D06 | Actual prior three-migration SQLite schema upgraded with existing manual job, history/pending-uncertain operation and verified backup preserved; model snapshot has no pending changes | Pass; normal older-executable schema guard retained. Manual EF downgrade is unsupported because dropping the ledger loses receipt guarantees |
| D07 | Coordinator terminal deletion, checkpoint, replay, status and exact 90/91-day prune boundary; old replay after prune cannot silently create a job | Pass; no new history-delete UI supplied |
| W05 | Actual SQLite abort and injected SQLITE_FULL (13); postcommit failure returns Unknown rather than false Rejected/Accepted; no orphan job rows | Partial: deterministic storage faults pass; physical full disk/runtime ACL failure not exercised |
| Network admission | localhost, IPv4 loopback including alternate literal spelling, private/link-local, IPv6 loopback/ULA/link-local/unspecified and IPv4-mapped IPv6 rejected before engine mutation | Literal gate passes. DNS/redirect/rebinding safety remains unresolved; do not enable production browser intake on this evidence |
| Existing behavior | All 94 Application tests, including manual Add cancellation, queue commands and uncertain-engine/recovery behavior; targeted retry-file preservation, RPC and upgrade tests | Pass for the listed suites; full Infrastructure, complete Verify and GUI launch not rerun |

## Checks

Run serialized and outside the restricted testhost context:

```powershell
dotnet restore AriaGui.slnx --locked-mode -p:Platform=x64 -p:NuGetAudit=false --ignore-failed-sources -m:1 -nr:false
dotnet build AriaGui.slnx --no-restore -p:Platform=x64 -p:NuGetAudit=false -m:1 -nr:false
dotnet test tests/AriaGui.Application.Tests/AriaGui.Application.Tests.csproj --no-restore -m:1 -nr:false --logger trx --results-directory artifacts/browser-wp2
dotnet test tests/AriaGui.Infrastructure.Tests/AriaGui.Infrastructure.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~ExternalIntakeTests|FullyQualifiedName~RpcTests|FullyQualifiedName~UpgradeTests|FullyQualifiedName~Retry_does_not_overwrite_an_existing_file_without_a_control_file' --logger trx --results-directory artifacts/browser-wp2
```

Locked restore passed. Solution/app build passed with zero warnings/errors. Application: 94/94. Targeted Infrastructure: 47/47, including 29 external-intake cases. TRX files are under `artifacts/browser-wp2`. Final reruns include stable NotReady receipts, exact session-source corrections and observable pre-claim failure. Tests use isolated temporary directories, real SQLite and deterministic engines/hooks; no user profile, browser, registry or network changes.

Before enabling browser transport, address DNS/redirect isolation and map domain receipts to the wire schema. Trusted caller identity and installation selection must be supplied by the later host security boundary; accepting caller identity from request JSON is forbidden. Full source URLs remain in the existing protected history/engine session storage; no additional raw URL is stored in the receipt ledger or emitted in diagnostics. Deployment/privacy protections remain later acceptance gates.
