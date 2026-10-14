# Five aria2-backed capabilities: phased implementation plan

2026-10-14 · Proposed work, not completed milestones. Architecture and technical source links: [design](ARIA2_FEATURE_ARCHITECTURE.md). Scope is five capabilities; Metalink is an optional metadata dependency/extension, not a sixth deliverable.

## Delivery order and scope

| Milestone | Deliverable | Dependency / exit gate |
| --- | --- | --- |
| M0 | Compatibility contracts and bounded engine experiments | Confirm baseline ownership/publication and freeze scope |
| M1 | Bandwidth profiles, then runtime-only schedules | Durable typed policy application |
| M2 | Expected SHA-256 verification and fresh-download repair | Publication cannot precede verification |
| M3 | Correct bounded ranges and fixed parallel HTTP | Range/recovery fault matrix passes |
| M4 | Conservative automatic connection choice | Measured improvement with bounded regression |
| M5 | Verified mirrors; later piece-based repair | M2; concurrent mixing additionally requires M3 |
| M6 | Torrent files, then magnets and seed controls | New manifest/ownership/network lifecycle accepted |

M1 and M2 are independently useful; ship them without waiting for torrents. Whole-attempt mirror failover can follow M2 without waiting for parallel HTTP. M4's initial promise is automatic choice and learning between attempts; live mid-transfer tuning is a separately gated extension. Piece repair can remain deferred if a trustworthy piece manifest is unavailable. M6 is the largest project and can be declined without compromising the HTTP roadmap.

No dates or effort estimates are asserted before the M0 experiments. Allocate implementation/review separately for transport changes and torrent lifecycle; do not combine them into a broad RPC refactor.

## M0 — Establish compatibility and prove engine behavior

Record the exact commit plus working-tree state, bundled executable hash, schema versions and supported NTFS boundary. Read the latest checkpoints rather than historical superseded sections of [continuity evidence](N03_CONTINUITY_IMPLEMENTATION_DESIGN.md) and [Phase C evidence](BROWSER_HANDOFF_PHASE_C_EVIDENCE.md).

Introduce typed capability/specification designs with default legacy mapping; do not yet change default dispatch. Capture version/features and required RPC support from the pinned engine, keeping binary/hash validation intact. Decide which new options are startup-only, safe live mutations or restart-causing operations; confirm with controlled fixtures. The manual explicitly warns that many active option changes restart transfers. [Official changeOption contract](https://aria2.github.io/manual/en/html/aria2c.html#aria2.changeOption)

Experiments use fresh synthetic runtime directories/private ports, owned origins and disposable files. Do not reuse the user's engine/listener, canonical handlers, personal browser profiles or production history. Tests must record actual range requests and bytes, not merely exit success or reported options.

Acceptance:

- Existing add/pause/resume/remove/retry/recover/queue behavior and ownership checks pass with all flags off.
- Sequential N01 resume obtains real nonzero 206; changed-200 and changed-206 cases preserve exact retained payload/control and never publish mixed bytes.
- Active connection-option changes are observed for restart, GID continuity, request order and preserved partials. Rate-limit changes and paused restoration are measured separately.
- Establish bounded range-at-zero behavior, duplicate/concurrent requests, cancellation and file/control snapshot behavior with sparse writes.
- Agree on initial cap/range/metadata limits and acceptance thresholds before interpreting performance results.

**Stop condition:** if the pinned engine cannot support a safe range/checkpoint boundary, retain sequential HTTP and ship M1/M2. Do not bypass the gateway to obtain an attractive benchmark.

## M1 — Bandwidth profiles and schedules

Add policy persistence and typed RPC operations beside existing queue policy. Implement requested/confirmed revision and generation reconciliation before UI presets. Provide aggregate caps, then per-job caps; show effective values and application failure. Keep all restored jobs paused until policy is established.

Next add local weekly intervals, explicit timezone, overlap rejection, temporary override expiry and separate policy-held/user-paused state. Evaluate current policy at startup/wake/clock change; no missed-event replay. Scope explicitly excludes running after application shutdown or waking the OS.

Acceptance:

- Apply/clear caps across active, waiting and recovered jobs; restart before RPC, after RPC and before confirmation. Unknown responses never claim success or fan out duplicate commands.
- Global and per-job constraints compose; queue count and per-file count remain distinct. Unlimited and Block are not confused.
- Manual pause/remove survives a scheduled transition; policy release resumes only eligible policy-held work.
- Fake-clock tests cover midnight, DST gap/repeat, changed timezone, sleep spanning boundaries, overlapping input rejection and override expiry.
- Measure aggregate upstream bytes over defined steady windows, excluding bounded startup buffers. Test two jobs and cancellation; document tolerance and enforce relay backpressure if aria2 alone cannot meet it.

Rollout: `BandwidthProfiles` first, `BandwidthSchedules` second. Kill switch removes scheduling authority without clearing manual pause or erasing saved policy. Gate default enablement on restart and upstream-rate evidence.

## M2 — Expected checksums and honest repair

Add expected SHA-256/provenance to manual intake and immutable attempt specifications. Implement staged verification with cancellation and file-identity/write-exclusion checks before publication. Persist verification outcome separately from transfer progress; adapt UI and completion notifications so verification is visible and required failures cannot appear Completed.

First repair action creates a fresh attempt and keeps failed/old files. It does not attempt selective repair from a whole-file digest. A later verification of an already published file is read-only until a separate repair/replacement operation is authorized.

Acceptance:

- Correct digest completes; one-byte corruption, wrong digest, malformed digest and changed staged file fail without public publication.
- No digest displays Not supplied, including all migrated historical jobs. Internal publication hashes never produce a publisher-verified badge.
- Crash/cancel before hashing, during hashing, after verification, after publication and before SQLite completion converges correctly; verification cannot be reused for a replaced file.
- Existing destination collision and hostile directory replacement retain old data; fresh repair preserves byte-exact original payload/control and records parent attempt provenance.
- Bounded hashing does not starve RPC/GUI; disk-full and access-denied outcomes remain recoverable.

Rollout: `ExpectedChecksumVerification`; required-integrity jobs cannot silently become optional when the flag is disabled. Keep them paused/pending with clear reason. This milestone is a useful release independent of Metalink.

## M3 — Range correctness before parallel speed

Extend transport request/response types to explicit intervals and versioned route/journal records. Keep old sequential readers. Implement immutable representation initialization, per-response validator checks, bounded concurrent admission, and request-specific cancellation. Preserve public/manual origin provenance and all actual-connection checks.

Replace length-only checkpoint triggering with revisioned quiescent payload/control snapshots that account for sparse writes. Reconcile range evidence with aria2's durable state; relay delivery alone is insufficient. Update guarded restore and legacy compatibility paths, active/stopped ownership validation, progress and publication checks together. Do not expand the old generic session parser to execute source-provided options.

Ship fixed 2/4-connection opt-in only after the correctness gate. Exclude one-shot bodies, unknown lengths and unsupported validators. Fallback before transfer can remain sequential; failures after partial acceptance retain evidence and stop or perform a separately proven same-representation recovery.

Acceptance:

- Real simultaneous disjoint ranges, including bounded offset zero, assemble exact expected bytes; no serialized pseudo-parallel pass.
- Reject ignored Range/200, changed ETag, changed effective redirect, weak/missing validators, encoded bodies, bad interval/total/length, multipart/suffix/overflow and truncated bodies before invalid bytes can contribute to publication.
- Race two initial responses; only one representation is committed. Cancel one range without abandoning unrelated owned ranges or leaving uncontrolled writers.
- Crash/kill at each durable checkpoint, including changed content with unchanged file length; restore paused with exact intent/policy and correct coverage.
- Preserve partial/control on identity failure; refuse foreign GID/path/route, journal tampering, missing metadata and directory replacement. Completed history cannot reactivate.
- Exercise body idle/total bounds, per-origin/global limits, loopback PID authentication, duplicate requests, and bandwidth-policy interaction.

Rollout: `ParallelHttpRanges`, disabled by default until the full preservation matrix passes. A disabled flag may restore compatible work sequentially only after explicitly proving its persisted representation/control format supports this; otherwise preserve and pause it. No direct-origin fallback.

## M4 — Conservative automatic connection policy

Introduce an explainable controller using origin timing, throughput windows, remaining bytes, active jobs, configured caps and observed failure/throttling. Begin with initial 1/2/4 selection and short-lived learning between attempts. Show chosen cap and reason; retain a manual override.

Before any later live adaptation, implement persisted reconfiguration intent, quiescence and restart revalidation. Rate-limit errors should trigger cooldown/backoff, not more concurrency. Avoid polling-frequency feedback loops and avoid rewarding loopback cache/buffer speed.

Acceptance:

- Controlled bandwidth/latency origins show improvement where parallelism helps and stay within agreed CPU/memory/connection overhead and throughput-regression thresholds elsewhere.
- Slow disk, small file, unknown size, user cap, server throttling and global bandwidth saturation do not cause oscillation/escalation.
- Multiple jobs obey per-origin and aggregate budgets; telemetry contains no raw URLs or capabilities.
- For live adaptation, crash between intent/reconfiguration/observation retains policy and data; pause/remove wins over queued tuning, and identity changes fail closed.

Rollout: `AdaptiveHttpConnections` after fixed parallelism. Keep live tuning behind a separate flag until measured. If gains are marginal, retain automatic initial selection and do not market continuous optimization.

## M5 — Verified mirrors and optional piece repair

First deliver bounded explicit source sets plus expected SHA-256 and whole-attempt failover. Persist each candidate and its origin policy; never infer equivalence from names/ETags. Prefer one logical gateway route with journaled source selection. If using multiple routes, first implement exact source-set ownership and restart recovery.

Then, if justified, allow concurrent mirror ranges behind M3 and M2's final verification gate. Limit repeated full-object failures; quarantine suspect source sets and require review rather than retrying indefinitely. A whole-file mismatch cannot identify the corrupt contributor.

Piece repair is a later submilestone: require a bounded trusted piece manifest, isolated repair copy, validated range fetches and complete final verification. A Metalink adapter may provide the metadata after an XML/path/URL validation review; it is not necessary for initial failover and is not counted as a sixth feature. [Metalink metadata standard](https://www.rfc-editor.org/rfc/rfc5854.html)

Acceptance:

- Two mirrors with equal length and different bytes/ETag behavior cannot publish under one expected digest. Same ETag text across hosts conveys no trust.
- Bad-first/good-second failover preserves the first stage and publishes the good result; expired/signed URL changes do not mutate a committed source revision silently.
- Every redirect/DNS target receives the correct public/manual policy; no authorization/cookies leak to another mirror. Changed mirror lists and restart preserve exact policy and identity.
- Concurrent mixed corruption fails final verification and never becomes Completed; retry budgets are bounded.
- Piece mode repairs only validated damaged pieces on a new work copy; wrong manifest/index/size/hash and cross-boundary corruption fail safely. Old payload/control remain identical.
- Optional XML import rejects entities, traversal, excessive nesting/count/size, unsupported hashes/schemes and executable/engine option injection; multi-file documents are rejected until supported explicitly.

Rollout flags: `VerifiedMirrors`, then `ConcurrentMirrorRanges`, then `PieceRepair`. Disabling any must retain required integrity checks and fail closed for unsupported active specifications.

## M6 — Torrent lifecycle, then magnet acquisition

Start with an architecture decision record covering network permission, stable private payload/public-copy lifecycle, disk reservation, file selection and completion/seeding semantics. Resolve this before enabling any discovery. Keep current HTTP/browser intake unchanged.

Add local `.torrent` parsing/preview with bounded manifests and path reservations. Create a dedicated engine adapter plus metadata/specification ownership, per-file progress, selection, upload/seeding controls and persistent state. Keep original metadata privately so recovery does not depend on engine session serialization. Pin support to demonstrated formats; reject unproven v2/hybrid inputs.

Next add magnet metadata-only acquisition and bind resulting metadata to requested identity. Stop at preview; only committed selection starts payload transfer. Register child GIDs only after lineage/manifest validation, including after restart. Trackerless magnets remain unsupported until a deliberate DHT/network-policy decision and tests; do not silently enable discovery process-wide.

Acceptance:

- Controlled local swarm tests cover single/multi-file torrents, selections, adjacent piece spill, no-selection input, metadata timeout/cancel, duplicate input and unknown GIDs. No public swarm is needed for development tests.
- Reject traversal, absolute/ADS/device paths, case collisions, oversized metadata, excessive files and reparse/foreign roots before writes or disallowed network contact.
- UI distinguishes metadata, checking, selected payload complete and seeding; aggregate progress does not exceed the selected content model unexpectedly.
- Zero post-download seeding, finite ratio/time limits, upload caps, manual stop, queue fairness and close/restart policy behave as specified. Do not claim zero upload during acquisition merely because post-download seeding is disabled.
- Kill/restart during metadata, selection, download, verification, publication and seeding; retain exact manifest/selection/ownership, reconcile children and publish only selected verified files once.
- Public copies can move/change without altering private seeding bytes; disk exhaustion and interrupted publication preserve prior files. Retention/cleanup occurs only after confirmed engine stop and explicit ownership checks.
- Validate tracker/web-seed/peer network policy, private-torrent handling, DHT/PEX/LPD state, port binding and metadata privacy. No firewall/router rules are silently installed.

Rollout: `TorrentFiles`, then `MagnetMetadata`, then separately `TorrentDiscovery` and `PostDownloadSeeding`. Shipping torrent support is blocked until privacy/network and storage decisions are implemented and tested, not just documented.

## Schema migration, rollback and test governance

Use additive SQLite migrations for specifications/sources/integrity/policy/manifests and explicit journal versions. Back up a quiescent database and retain journals before migration. Validate mixed legacy/new jobs and crash interruption. Bind database rows and journal to attempt/spec revision. Existing terminal history retains its original meaning; never recompute it into Verified or seedable status.

Older binaries must refuse unsupported schema/specifications safely. Flag rollback is not data-format rollback: retain new readers and paused state, or restore a proven complete backup only after ensuring no live writer. Never rewrite a new journal as an old sequential record or discard metadata to make downgrade succeed. Feature disablement does not revoke retention/publication safety.

For each milestone run focused unit tests, controlled origin/swarm integration tests and pinned real-engine recovery tests. Before default rollout run the existing repository verification entry point and applicable native/package checks in supported isolated Windows environments. Record exact binary/build/OS, passed/failed/skipped cases and artifact paths. A missing environment is an incomplete gate, not a pass. Fault injection must include publication-before-database-commit and lost-RPC-response windows; preservation assertions compare bytes, not just sizes.

Maintain separate browser/Phase C acceptance: these features must not enable the production browser listener, broaden `ariagui://open`, forward credentials, install handlers or claim installed acceptance. Finish that work through its existing runbooks. No changes to existing Phase C files are required by this proposal.

## Explicit non-goals

No arbitrary aria2 options UI; unguarded origin bypass; transparent reuse of unauthenticated browser/legacy partials; automatic public mirror search; repair without expected integrity; background service/OS wake; browser magnet/Metalink takeover; automatic metadata following; remote RPC; new engine ownership mechanism; network/cloud filesystem expansion; anonymous P2P claims; or silent engine upgrades. Each would need its own bounded design and acceptance gate.

This document plans future verification. It reports no new test passes or shipped capability. The review itself changed only these two new design documents.
