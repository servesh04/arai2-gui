# Five aria2-backed capabilities: architecture and design

2026-10-14 · Proposal, not implemented behavior. Companion: [phased implementation plan](ARIA2_FEATURE_IMPLEMENTATION_PLAN.md). This review reads the current working tree, including uncommitted browser/Phase C work; it does not certify that work or change its gates.

## Recommendation

Build bandwidth profiles and expected-checksum verification first. Add bounded parallel HTTP only after extending the guarded transport and recovery journal. Verified mirrors then reuse those foundations. Treat torrent/magnet support as a separate transfer lifecycle with a larger storage and privacy scope. Metalink is a possible metadata adapter for mirrors/piece hashes, not a sixth product feature.

All five are feasible in bounded forms. Three earlier-sounding promises need qualification: automatic connection tuning is not a harmless option toggle; an arbitrary HTTP checksum cannot identify damaged pieces; torrent support is not merely admitting magnet URLs to the current Add dialog.

## Current implementation and constraints

| Boundary | Observed implementation | Consequence |
| --- | --- | --- |
| Application contracts | `IAria2Client`, `EngineDownloadRequest`, `DownloadJob`, `DownloadFile`, `DownloadEntry` describe a single source and destination; commands persist intent before mutation | Introduce explicit transfer kinds and typed policy, not an unrestricted options dictionary |
| RPC | `Aria2RpcClient.AddAsync` supplies one URI, disables torrent/Metalink following and sets both connection options to one. Progress asks only for GID/status/length/speed/error | New capabilities need richer observations and explicit commands |
| Ownership | Live/stopped checks require one file at the expected absolute path; every URI must equal the expected source | Multiple sources, metadata children and torrent manifests cannot pass by loosening these checks globally |
| Guarded production path | `Aria2ProcessOptions.ForApplication` enables `ContinuityLoopbackGateway`; `Aria2ProcessManager` substitutes a private capability route and private work path through `ContinuityTransferLedger` | Keep origin policy and publication under application control |
| Relay | `HttpContinuityRelay` serializes the whole response. Resume binds effective resource, strong ETag, identity encoding and length. A one-shot body cannot be replayed | Concurrent disjoint ranges require a new versioned protocol |
| Publication | Ledger preserves retained payload/control, pins directory identities/handles, journals publication, and disables completed routes. Local SHA-256 protects journal/publication consistency | These hashes are not an expected publisher checksum or proof of authenticity |
| Recovery | Guarded `PrepareRestoreAsync` reconstructs paused routes from durable application ownership. Legacy `SessionRecoveryPlanner` admits known HTTP(S) records only and emits restricted paused options | Update both paths deliberately; never execute imported session options |
| Persistence | SQLite separates jobs, attempts, files, operations, queue policy and external/prepared intake; runtime journals own transport/publication details | Extend durable intent and journal schema together; one source string is insufficient for mirrors |
| Engine policy | Bundled engine is pinned 1.37.0; startup disables DHT, DHT6, peer exchange and local peer discovery | Capability experiments must use this binary; no silent upgrade or network expansion |

Primary code anchors: [RPC](../src/AriaGui.Infrastructure/Aria2/Aria2RpcClient.cs), [manager](../src/AriaGui.Infrastructure/Aria2/Aria2ProcessManager.cs), [relay](../src/AriaGui.Infrastructure/Downloads/HttpContinuityRelay.cs), [gateway](../src/AriaGui.Infrastructure/Downloads/ContinuityLoopbackGateway.cs), [ledger](../src/AriaGui.Infrastructure/Downloads/ContinuityTransferLedger.cs), [legacy recovery](../src/AriaGui.Infrastructure/Aria2/SessionRecoveryPlanner.cs), [contracts](../src/AriaGui.Application/Downloads/IAria2Client.cs), [database](../src/AriaGui.Infrastructure/Persistence/DownloadDbContext.cs).

The current [continuity evidence](N03_CONTINUITY_IMPLEMENTATION_DESIGN.md) supports a guarded manual path on direct local NTFS destinations. It does not establish arbitrary network/cloud/reparse support. Preserve that supported subset. Browser-public origins reject private addresses at each actual connection; trusted manual intake permits unicast LAN/loopback. Preserve provenance rather than applying browser rules to all manual jobs or allowing browser callers to select manual policy.

[Phase C evidence](BROWSER_HANDOFF_PHASE_C_EVIDENCE.md) records a narrow installer candidate and synthetic acceptance, with installed canonical activation, Explorer and clean-machine acceptance still pending. Open-only activation remains open-only; browser production acceptance, deferred Phase A takeover and blocked direct credential forwarding in Phase B are separate work. These proposals do not close or expand any of those gates.

## Invariants and common design

1. The existing GUI/engine leases, authenticated loopback RPC, binary pins and exact ownership proof remain authoritative. A GID alone never establishes ownership; unknown transfers remain untouched.
2. An attempt has an immutable specification and revision: transfer kind, source identities, destination manifest, expected integrity, origin policy and approved transfer policy. Intent commits before RPC; an uncertain response is reconciled against that revision and engine generation before retry.
3. Retained partial/control pairs remain immutable backups. Changed identity, missing metadata or unsupported versions produce a reviewable stop, never automatic overwrite, control-file deletion or fresh restart. Explicit fresh restart keeps the old pair and uses an empty stage.
4. Completion has separate meanings: transport ended, integrity checked, publication committed. Only the last applicable state becomes user-visible completed. Journal replay must converge after publication but before SQLite completion.
5. Keep URLs, signed queries, route capabilities, tracker passkeys and metadata private. Diagnostics use bounded reason codes and aggregate counters; no raw RPC error strings or imported option text. No browser credential forwarding is added.

Proposed contracts are small typed records: `TransferKind`, `HttpSourceSet`, `IntegrityRequirement`, `HttpConnectionPolicy`, `BandwidthPolicy`, `TorrentManifest`, and a versioned attempt specification. Preserve existing records at compatibility adapters until each new kind is supported end-to-end. Add verification and torrent substate independently of existing serialized status enum values; do not renumber them.

SQLite owns user intent, policy revisions, sources, expected digest/provenance and file selections. The private journal owns representation validators, range/verification evidence, filesystem identities and publication phases. A shared attempt/specification digest binds both; neither store silently overrides a disagreement. Version all new journals and capability routes. Existing jobs remain sequential single-source HTTP with no expected checksum, not retroactively verified.

## 1. Automatic adaptive per-file HTTP(S) connections

### Why changing `split=1` is unsafe

The gateway reads a range start but does not use its optional end; it serves start-to-EOF and returns 200 at offset zero. The relay validates only full-body or tail responses and holds a single semaphore during body transfer. Simply increasing connections could serialize requests, time them out or violate bounded range semantics. Removing the lock alone introduces races in representation initialization and receipts.

The ledger captures changed partials using payload length. Parallel sparse writes can change content/control state without changing length. Existing completion length and sequential receipt assumptions cannot establish range coverage. Both recovery writers hardcode single-connection options; startup could therefore silently change the execution policy. RPC ownership is also capped at 16 identical URI entries. Every affected invariant needs a pinned-engine test before widening it.

The official manual states that changing most active options, including split settings, restarts the transfer; rate limits are exceptions. Therefore per-sample live retuning is not a safe initial design. [aria2 changeOption](https://aria2.github.io/manual/en/html/aria2c.html#aria2.changeOption)

### Proposed range transport

Keep aria2 as the piece scheduler and writer. Add a guarded range-capable route version; do not build a second download engine. Establish one immutable representation before parallel admission. Each request uses a validated start/end pair, and every origin response must match the exact effective URL, strong validator, total length, identity encoding and requested interval before its body is exposed. Reject multipart/suffix/overflow requests initially. A bounded request beginning at zero must receive correct 206 semantics. Reject a resumed/ranged 200 before delivering bytes.

Range support is established from actual responses, not `Accept-Ranges` or HEAD alone. Strong validators apply to a particular representation; HTTP permits a server to ignore Range and return a full response. These are protocol constraints, not proof of malicious behavior. [RFC 9110, range requests](https://www.rfc-editor.org/rfc/rfc9110.html#section-14), [If-Range](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.5)

Use a short initialization lock plus bounded per-attempt and per-origin admission. Preserve connection-time DNS/address validation, redirect policy, certificate validation, cancellation, byte limits and idle deadlines. Existing one-shot/unknown-size/no-strong-validator jobs stay sequential. After bytes have been accepted, representation failure stops the attempt and preserves it; fallback cannot silently discard or combine representations.

Checkpoint a quiescent payload/control snapshot with a revision even when length is unchanged. Maintain range coverage evidence sufficient to reject holes/overlaps outside the requested plan, but do not mistake bytes sent by the relay for bytes durably written by aria2. Completion requires authenticated engine state, reconciled control/progress evidence and staged integrity checks. A trustworthy final expected digest adds a stronger gate where available; without one, label the result as continuity-checked, not publisher-verified.

### Bounded adaptation

First offer an automatic initial choice of 1, 2 or 4 connections for eligible sufficiently large files, with a manual cap. Bound aggregate connections across jobs and by actual origin; all loopback routes otherwise look like one server to aria2. Cache only short-lived origin-level measurements, not signed paths or credentials. Use upstream timing/bytes, because loopback read speed can misrepresent remote performance.

Measure a stable baseline; increase only after a substantial sustained benefit, and back off on repeated range failures, throttling or no gain. Minimum sample windows, cooldown, hysteresis and maximum changes per attempt must be explicit tested policy. Initial shipping scope learns between attempts; mid-transfer adaptation is a later gate requiring durable reconfiguration intent, quiescence, restart/revalidation and no loss of user pause/remove intent. A bandwidth cap or slow disk must suppress futile connection escalation. No guarantee of acceleration on every origin.

## 2. Verified multi-source/mirror downloads

Model a bounded source set for one content identity, not multiple independent jobs. Require a trusted expected SHA-256 for the initial feature. The user supplies or explicitly accepts its provenance. Same filename, length, Last-Modified or matching ETag strings across different origins do not prove identical content. Per-origin validators protect continuity within each mirror; a common expected digest binds the final object across mirrors.

Begin with whole-attempt failover: preserve the failed stage, start the alternative in a new stage and publish only on digest match. This is useful before cross-mirror partial reuse exists. Concurrent range mixing needs the range transport and final digest gate; it can detect a corrupt output but cannot identify which mirror supplied bad bytes without piece evidence. Do not promise automatic fine-grained fault attribution from a whole-file hash.

Prefer application-owned mirror selection behind one logical guarded route, with the source-set revision bound in the journal. This preserves one engine URI ownership check while making origin choices auditable. Do not disguise several origins under a fabricated shared HTTP validator: retain per-origin metadata and a separate content identity. If multiple capability routes are chosen instead, add exact allowlisted route-set ownership and recovery; never replace exact comparison with arbitrary URI membership.

Validate every mirror and redirect under the job's existing origin policy. Do not transfer cookies, authorization or referrers across hosts. Default to explicit HTTPS mirror lists; reject downgrade and bound source count, redirects, retries and probing. A changed source list becomes a new persisted revision, not an in-memory RPC change. No web scraping or automatic public mirror discovery.

## 3. Checksum verification and repair

First add an optional expected SHA-256 field at manual intake, with strict hex/length validation and explicit provenance. Compute it over the closed, identified staged file before `complete-prepared`/publication; persist algorithm, expected/actual result and file identity. Cancellation leaves verification pending and preserves work. Revalidate identity and exclude an active writer before trusting the result; do not hash a mutable public path and then publish different bytes.

Expose Not supplied, Pending, Verifying, Verified and Mismatch. Existing journal hashes remain internal consistency evidence. A checksum supplied alongside compromised bytes does not authenticate a publisher. Imported digest algorithms require an allowlist; do not present weak legacy digests as strong authenticity evidence.

Whole-file hashes detect mismatch but cannot locate corrupt pieces. Repair with only such a hash means a fresh download; piece hashes enable targeted repair. [aria2 integrity behavior](https://aria2.github.io/manual/en/html/aria2c.html#cmdoption-check-integrity)

For the first repair action, keep the failed stage/old destination and offer a new attempt. Never enable an engine mode that silently redownloads over retained protected files. Later piece repair requires a bounded trusted piece manifest (algorithm, length, indexes, hashes, total/content identity), hash checks on an isolated repair copy, and writes only into new owned work. Recheck the complete expected digest before publication where supplied. Repairing an already published file is a new operation with an explicit destination replacement/conflict decision; no implicit reacquisition of historical Completed ownership.

Metalink can supply URLs and piece metadata through a bounded import adapter. Parse without DTDs/external entities, reject dangerous paths/options and unsupported schemes, and translate to the same typed source/integrity model. An untrusted manifest is not trusted merely because its checksums are internally consistent. The Metalink standard defines whole-file and piece hash metadata; importing it does not itself solve trust or safe repair. [RFC 5854](https://www.rfc-editor.org/rfc/rfc5854.html#section-4.1.5)

## 4. Bandwidth profiles and scheduling

Add named profiles with aggregate download/upload caps, optional per-job caps and queue concurrency. Keep queue count, per-file connections and byte-rate limits distinct. Encode unlimited explicitly in the application; a schedule's Block action is a pause policy, not a zero-valued speed limit. Proposed precedence: temporary manual override until its stated expiry, then matching scheduled profile, then default. Per-job caps further constrain the aggregate policy.

Use typed `changeGlobalOption`/`changeOption` adapters and observed effective values; persist requested/confirmed revisions and uncertain application state. Apply caps before recovered jobs resume. On engine restart or wake, recompute the current profile once; do not replay every missed boundary. A failed application remains visible as pending/failed, not a falsely enforced cap.

Schedules initially run only while the app/owned engine runs. Store timezone ID and weekday/local intervals; reject overlaps in the first editor. Define DST as evaluating the current local wall-clock interval: skipped intervals are not replayed, repeated intervals have the same rule. Reevaluate after clock/timezone changes. A manual pause survives schedule changes; track policy-held state separately from user pause. Removal wins over both. Sleeping/shutdown scheduling, OS wake and a background service are non-goals.

Enforce and measure the rate at the actual transport boundary. Aria2 caps apply to its loopback receive side; bounded relay buffering/backpressure must keep upstream traffic within a documented tolerance. If tests show material upstream overshoot, add one shared relay limiter tied to the same policy instead of claiming a network cap. Persist no duplicate independent scheduler. Global upload policy also constrains future torrents.

## 5. Torrent/magnet, selective files and seeding

Use a distinct transfer kind and capability-gated adapter, not the HTTP relay. Add explicit states for metadata retrieval, awaiting file selection, downloading, checking, download complete/seeding, stopped and error. Preserve separate selected-byte progress, upload rate/total, ratio and seed duration. An RPC active state must not always mean downloading.

Proposed sequence: bounded local `.torrent` intake, manifest preview and selection; later magnet metadata-only acquisition, then the same preview. Bind metadata to its infohash and persist it privately before data dispatch. Metadata downloads and payload children need durable parent/child engine ownership, exact manifests and recovery rules. Never adopt arbitrary `followedBy` IDs without checking expected metadata lineage. The BitTorrent format defines file/piece metadata; magnet metadata exchange is a separate protocol phase. [BEP 3](https://www.bittorrent.org/beps/bep_0003.html), [BEP 9](https://www.bittorrent.org/beps/bep_0009.html)

Reject absolute/traversal/device paths, ADS, reserved names, case-insensitive collisions, reparse targets and excessive file count/path/metadata sizes before writes. Reserve a new private root for the entire manifest, including incidental neighboring piece bytes, and hold directory identities throughout. Selected files can require adjacent-file bytes; prevent any automatic deletion of unselected files. [aria2 file selection](https://aria2.github.io/manual/en/html/aria2c.html#cmdoption-select-file)

The first lifecycle keeps the private payload root stable through seeding and publishes verified copies of selected files, accepting explicit disk overhead. No hard links between public mutable files and the seeding root. Retain publication/seed checkpoints until the engine stops; do not use HTTP's terminal route-revocation rule to discard still-owned torrent state. Before release, decide whether this disk cost is acceptable; moving the live payload instead needs a separately proven stop/relocate/recheck protocol.

Default to no post-download seeding until explicitly enabled. Present upload while downloading and peer IP exposure before network participation; encryption is not anonymity. Ratio/time controls end on either configured condition, and `seed-time=0` stops post-download seeding. [aria2 seed policy](https://aria2.github.io/manual/en/html/aria2c.html#cmdoption-seed-time)

Keep discovery disabled initially; support controlled tracker-based tests first. Tracker URLs, web seeds, DNS and peer destinations introduce network paths outside the HTTP guard. A design gate must choose enforceable destination restrictions or a clearly separate, explicit broader P2P network permission; do not advertise the existing browser-public SSRF guarantee for raw torrent networking. Unsupported/private-network metadata must fail before contact under a restricted policy. DHT/PEX/LPD, trackerless magnet usability, inbound port/firewall behavior and private-torrent rules require explicit review. No automatic firewall/router changes.

Persist metadata independently of engine session files. Avoid relying on `addTorrent` session persistence side effects, and test its metadata-save behavior in a private owned directory. Unknown torrent formats, including unverified v2/hybrid support, remain rejected until the pinned binary is proven compatible. No automatic torrent following, browser magnet association or mixed HTTP/torrent swarm download in the first release.

## Decision and release boundaries

Required decisions before implementation: range checkpoint proof; checksum provenance UX; initial mirror failover versus concurrent mixing; runtime-only schedule semantics; torrent network permission and publication disk strategy. These are product/architecture choices, not hidden defaults.

Use new-feature flags independent of continuity protection and browser enablement. Disabling a flag blocks new feature dispatch; existing incompatible attempts pause with their state intact. It must never route them through unguarded HTTP or downgrade journal formats. Preserve reader support or refuse older application startup after a schema upgrade. Rollout gates and concrete acceptance tests are in the companion plan.

Research was checked against the official [aria2 manual](https://aria2.github.io/manual/en/html/aria2c.html) and linked primary standards on 2026-10-14. Runtime behavior remains subject to focused tests against this repository's pinned Windows build; online documentation is not acceptance evidence. No new engine, GUI, installer or live-network tests were run for this design review.
