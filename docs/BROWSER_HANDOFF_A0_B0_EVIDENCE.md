# A0/B0 review corrections

Implemented 2026-10-13 on `2753957` plus the uncommitted Phase B investigation and review documents. Automatic takeover remains unregistered/unadvertised; no browser cancellation, downloads/cookie permissions, credential gateway or Phase C work was added. B0 is closed as negative feasibility evidence: **S01 blocked; S02 blocked; direct aria2 headers rejected**.

## A0 changes

The single existing SQLite repository now exposes one cleanup operation. Each indexed sweep expires at most 64 reservations, releases their destination rows and clears the live serialized payload/hash. It runs after repository initialization, once at maintenance startup, every ten seconds under app lifetime, and on wake. Shutdown cancels/awaits maintenance before disposing services. Storage failures in the periodic task retry on the next interval with a safe status message. No takeover admission service is registered.

Status reads no longer sweep or acquire a write transaction for an unrelated/live/terminal key. An expired target is reread and expired under a transaction. Commit and abort independently check their own row; backlog progress cannot authorize an expired commit. Explicit/manual intake sweeps are also capped. Startup/wake may leave a backlog for later batches; this is bounded housekeeping, not guaranteed prompt physical erasure.

Atomic admission quotas are 16 per caller/installation pair, 32 per caller, 64 per installation and 256 globally. Indexed bounded counts exclude terminal history. Expired-but-unswept prepared rows conservatively consume capacity; the internal busy receipt is retryable and creates no row. Same-key duplicate resolution precedes quota admission. These conservative disabled-domain limits are not browser release performance guarantees.

Prepared intent uses a version-one envelope and validates job/attempt/file/operation associations before commit. Unknown schema/hash/identity inconsistencies cannot authorize an engine request. Migration `20261013050740_PreparedTakeoverBounds` adds expiry/quota indexes and invalidates old unversioned prepared reservations, clearing their live payloads/reservations. Accepted jobs and terminal receipts remain. The normal migration backup intentionally retains old bytes. An older executable rejects the newer schema through the existing guard.

The expiry policy is a fixed UTC deadline, preserved across duplicate calls/restart. Clock rollback can lengthen actual elapsed lifetime; a forward jump can expire early. Tests cover both and overdue startup cleanup. A process-local monotonic timer is not represented as restart-safe. Actual Windows sleep/wake timing remains unaccepted; wake cleanup is composed and compiled.

Commit duplicate metadata identifies which transaction changed the reservation. History deletion preserves committed responsibility with an inner expired tombstone receipt and no job ID; commit/abort/status cannot resurrect it. After the existing 90-day tombstone pruning policy, stale prepare fails admission and commit is not-found. Explicit/takeover request keys conflict across action families. Internal phases/results are not a new wire design.

Normalization now returns the same canonical trigger that it validated. The continuity relay comment now accurately describes bounded redirect handling and makes clear that its constructor checks initial source shape; transport and representation validation bind the eventual resource. No credential redirect policy is inferred from that constructor.

## Retention and domain verification

Live intent clearing is logical cleanup only. A synthetic signed-source fixture exercises DB/WAL, upgrade backup and payload clearing. `artifacts/browser-a0/retention-evidence.json` records marker-presence booleans, never marker values. A retained migration backup preserves the source after the live row is cleared. No SQLite free-page, backup, filesystem or physical-erasure guarantee is made. Before activation, product policy must explicitly accept retained aborted-source artifacts or approve a different durable source design; this package does not grant credential retention.

The domain tests cover idle cleanup with no incoming calls, cancellation of maintenance, all four quota scopes, target expiry amid backlog, index-plan use, UTC changes/restart, schema/association corruption, destination file/control-file/directory replacement, preference changes, tombstones/reused keys and concurrent transactions. Six additional tests launch a separate fixture process and actually kill it at prepare/commit/abort before/after transaction commit, then reopen SQLite and reconcile exactly zero or one intent/engine Add. Existing exception-hook tests remain separate evidence.

The bounded-load fixture has 10,000 terminal ledger rows and 151 prepared rows. One sweep clears 64; a target lookup clears at most its own remaining row. `artifacts/browser-a0/load-evidence.json` records actual developer-machine latency and expiry-index use. This is not worst-case production latency under arbitrary I/O contention or an unlimited historical database.

## B0 changes

The synthetic pinned-engine harness requires every documented field in each lifecycle session, both restore copies and restored `getOption`, plus each same-origin/cross-port/cross-host/HTTPS-downgrade target. It saves per-case checks and returns changed/inconclusive if observations differ; zero means measured blockers, never safe credentials. TLS/global test certificate handling remains fixture-only.

The wrapper scans actual diagnostics exports for current log and a retained copy named `engine.log.previous`, requires custom-header/User-Agent retention in the applicable cases, and verifies an unknown synthetic exception transport log is excluded by the existing export allowlist. It adds no production regex or richer context support. Actual engine rotation, production exception pathways, browser cookie selection, app credential suspend/recovery/DB artifacts and volatile transport isolation remain outside this negative spike.

## Commands and results

```powershell
dotnet build src/AriaGui.App/AriaGui.App.csproj --no-restore -p:Platform=x64 -m:1 -p:UseSharedCompilation=false
dotnet test tests/AriaGui.Application.Tests/AriaGui.Application.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false --logger trx --results-directory artifacts/browser-a0/application-test-results
dotnet test tests/AriaGui.Infrastructure.Tests/AriaGui.Infrastructure.Tests.csproj --artifacts-path artifacts/browser-a0/isolated-build -p:RestoreLockedMode=true -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~PreparedTakeoverTests|FullyQualifiedName~UpgradeTests|FullyQualifiedName~ExternalIntake' --logger trx --results-directory artifacts/browser-a0/final-test-results
./scripts/Test-BrowserCredentialFeasibility.ps1 -ArtifactDirectory artifacts/browser-phase-b/b0-final
git diff --check
```

App build: zero warnings/errors. Application regression suite: 97 passed. Final focused SQLite suite: 83 passed, including all six abrupt child termination cases and competing-writer quota admission. Browser contracts: 94 .NET and 23 JavaScript fixture tests passed. B0 wrapper: completed with `observed_blockers`, all seven lifecycle and four redirect checks true, both gates blocked.

Retention scan: source marker absent from main DB and present in WAL before/after upgrade, present in migration backup, with live payload cleared. Bounded cleanup plus target status measured approximately 64 ms on this developer machine. Both values are fixture observations, not universal guarantees.

An earlier broader run included the affected `RealQueueTests`/`RealDownloadTests`: 137 passed and one retention scan failed because its original reader did not share Windows file handles. The scan was corrected and passes in the final focused run. That broader run retained the usual test DLL, so two attempted concurrent normal-output rebuilds hit file-copy locks; final verification uses the separate locked-dependency artifacts tree shown above. No real-engine assertion failed, and no historical test result is silently relabeled passed.

## Remaining gates

A1/A2 remain entirely unstarted: trusted replayability/grant/context design, durable browser reducer/adapters, fresh browser-specific cancellation evidence and A01–A04 in actual throwaway browser profiles. Takeover activation also needs a signed-source retention decision, actual GUI suspend/wake/shutdown and contention acceptance, and limits review for supported workload. Domain tests do not close these gates.

B1 is not authorized or implemented. The gateway remains an unproven credential candidate; terminal-route retirement/capacity and durable capability/restart isolation remain its prerequisite work. Structured safe richer-context diagnostics and full actual lifecycle credential evidence would be required for any future transport. Phase C remains deferred. All changes remain uncommitted.
