# Package 8: automatic-takeover reservation foundation

Baseline: `1339e09`. Package 8 means implementation-plan section 10 (Later phase A), not a previously numbered package. This is an application-domain slice, **not automatic browser takeover release acceptance**.

## Implemented boundary

`PreparedTakeoverService` requires both trusted application policy and saved browser/automatic-takeover consent. Both default off. Only an exact resource in a trusted `ReplayablePublicGetGrant` is eligible; a URL suffix or caller assertion of method/authentication is insufficient. Grant provisioning and browser proof of replayability remain future work. Private contexts, extension-origin items, malformed opaque profile identity, private literal targets and changed item/source/name are refused.

The service is deliberately not registered in App composition, not exposed by the pipe/native host, and absent from the advertised v1 schema. The extension has no new downloads permission/listener and performs no cancellation or erasure. Existing production-browser gates stay closed.

Prepare validates the local destination and persists a 60-second reservation in the existing SQLite external-request ledger. It creates no job, attempt, file or pending engine operation; it invokes no engine or origin probe. Signed source query spelling/order survives normalization, persistence and committed engine request. Abort/expiry clears the live serialized reservation source on logical cleanup; SQLite WAL, free pages and prior backups are not certified physically erased. At this Package 8 baseline, idle cleanup was lazy until the next repository operation. No new source URL logging or browser-local persistence is introduced.

Follow-up dated 2026-10-13: [A0/B0 review corrections](BROWSER_HANDOFF_A0_B0_EVIDENCE.md) add bounded indexed startup/periodic/wake cleanup, quotas, intent versioning and status/retention/crash tests. This does not retroactively change the historical Package 8 acceptance counts or enable browser takeover.

Commit requires the same payload/item identity and domain-level `Cancelled` evidence, repeats local destination validation, and atomically converts the reservation into the existing ledger + job/attempt/file/pending-add transaction. A future trusted browser adapter must re-read actual browser cancellation; a fulfilled cancel call or this domain record is **not browser API proof**. The committed receipt uses existing app-lifetime dispatch/readiness and status recovery; client cancellation cannot revoke durable acceptance. Abort/expiry cannot remove accepted work. Independent SQLite writers serialize the commit/abort/expiry decision; duplicates retain the original expiry/receipt.

Expiry is evaluated under the write transaction at operation admission. Physical expiry cleanup is lazy on lookup and intake/manual writes, not a background timer. Reservations therefore stop admitting commit at 60 seconds, but idle expired records may retain the intent until the next such operation. No active feature exposes this limitation yet; background scrubbing is required before activation.

## Database change

Fifth migration: `20261012153721_PreparedTakeover`, with nullable takeover phase/expiry/hashed bounded serialized intent on `ExternalRequests` and a `PreparedDestinations` table linked to the same caller/installation/request key. SQL triggers and transactional manual/explicit-intake checks protect reserved destinations. Existing migration backup and future-version refusal behavior remain tested. No user database was opened or migrated during development; fixtures use isolated temporary databases.

## Evidence

- Reservation/intake/upgrade run: **61/61 passed** (26 new prepared-takeover cases plus existing external-intake/upgrade cases). `artifacts/browser-wp8/prepared-intake/prepared-intake.trx` and `prepared-intake.log`.
- Serialized full solution build: **passed, 0 warnings / 0 errors**. `artifacts/browser-wp8/build.log`.
- Final affected Infrastructure regression: **67/67 passed**, including the above cases, real-engine manual pause/resume/remove/retry, migration and integrated strong-entity N01 / ignore-Range N02 / changed-entity N03 preservation cases. `artifacts/browser-wp8/affected-regression/affected-regression.trx` and `affected-regression.log`.
- Application suite: **97/97 passed**. `artifacts/browser-wp8/application/application.trx` and `application.log`. No full Verify/browser/extension acceptance run was claimed; unchanged v1/extension code received no new permission or protocol surface.
- Self-review: transaction and signed-source boundaries, migration/model agreement, opt-in composition absence, and diff whitespace checked. No commit or production registration was made.

New real-SQLite cases cover default-off/refusal, no pipeline work before commit, reservation across restart, signed query identity, concurrent prepare/commit, competing destination/manual add, exact expiry, changed browser item, commit/abort race, six before/after-commit fault boundaries, durable reopen/status, client cancellation rollback vs accepted-after-commit, migration backup and direct SQL reservation protection. Fault hooks are constructor-only; these tests simulate database/process-boundary exceptions and reopen, **not abrupt OS process termination or actual browser worker death**.

## Acceptance status and next work

| Gate | Status |
|---|---|
| Prepared/no-origin/no-engine and duplicate transaction boundary | Passed domain/SQLite tests |
| A01 completion wins | Domain changed/completed item refuses commit, abort creates no job; actual browser race untested |
| A02 pause/cancel failure/disappearing item | Domain abort/invalid phase tested; actual browser APIs/resume UX unimplemented |
| A03 canceled then app/host dies | Durable transaction fault/reopen and lost-ACK status tested; browser pending record, transport and recovery UI unimplemented |
| A04 extension/fallback loop | Extension-origin item refused in domain; onCreated listener absent, real browser loop test untested |

Next coherent slice must define a versioned takeover wire capability, trusted browser-item/replayability adapter, optional permission/consent UI, minimal durable browser pending identity and cannot-confirm recovery UI, then exercise A01–A04 in throwaway browser profiles. Browser canceled + app unavailable beyond expiry remains an explicit loss risk: preserve browser history/partial files, never claim atomic fail-open, and never blind-restart an unknown commit. Current browser public-network/trust/release gates remain applicable. Package 8 does not enable them.
