# M2 expected SHA-256 verification — acceptance evidence

Date: 2026-10-15. Base commit: 011d8db (M1). The M2 working tree is uncommitted; unrelated Phase C changes were present before M2 and have not been staged or committed here.

## Decision

The implementation and automated M2 gates are complete. Interactive WinUI visual and keyboard checks remain for the user; do not mark the M2 UI gate passed until that observation is recorded. This evidence does not establish installed-package acceptance or an actual exhausted-volume test.

## Behavior

- Manual Add accepts an optional exact 64-hex expected SHA-256. The job stores the normalized digest and UserSupplied provenance; each attempt stores NotSupplied, Pending, Verifying, Verified, or Mismatch separately from transfer progress. Historical jobs migrate to NotSupplied without gaining a verification claim.
- Required-digest jobs use the guarded private stage and cannot dispatch through direct mode or with verification disabled. Restore holds such work with an explicit per-row reason. Disabled mode cannot replay a required publication checkpoint. An unverified Completed engine observation is rejected by the coordinator.
- At authenticated engine completion, at most two background workers hash identified, write-excluded private files. Polling promptly reports Verifying; publication waits for the correct digest, durable checkpoint, and rechecked file identity. The UI inspector and completion notice distinguish a match to the supplied digest from publisher authentication.
- A mismatch leaves the private stage and any public payload/control intact. Retry creates a child attempt with a fresh representation record and retains the failed stage. A destination collision or changed directory identity prevents publication without replacing the existing files.
- Durable verified, prepared, moved, and published receipts recover across restart. If SQLite completion was lost after publication, only a known database-bound GID with a valid original public file identity and digest can synthesize a completion observation. A replaced file or conflicting job intent cannot do so.

## Automated checks

- A synthetic fixture at `tests/AriaGui.Infrastructure.Tests/ChecksumFixture/m2-synthetic.bin` is 131,072 deterministic bytes; SHA-256 is `821BA32C43E025C60AC06EB3BA0F9E716D4857D10065AB3A87F952E2881C244D`. A focused integration test serves the fixture through the local test server to the pinned aria2 engine. On 2026-10-15, both cases passed (2/2): the exact digest completed with `Verified` and a byte-for-byte identical published file; a digest with its first hex digit changed failed with `Mismatch`, left no public file, and retained a byte-for-byte identical private stage.

- The repository scripts/Verify.ps1 entry point passed: locked restore; x64 solution build with zero warnings/errors; Core **16/16**, Application **163/163**, Infrastructure **379/379**, Browser.Contracts **94/94**; shared JavaScript fixtures **23/23**; extension tests **99/99**. The script generated development extension templates but did not register a browser.
- Before that full run, a clean standalone Infrastructure run passed **377/377** after fixing all eight initial failures. The full script included the later cancellation and storage-fault cases. A one-byte corruption variant changed after the script build was rebuilt and passed its targeted test **1/1**.
- Pinned-engine tests cover correct/mismatched digests, durable recovery, direct/disabled intake rejection, fresh repair with ParentAttemptId, byte-exact preservation of a failed stage, existing public payload/control collision, and a fault exactly before SQLite records Completed.
- Publication tests cover wrong digest, changed staged file, same-byte file replacement, conflicting DB/journal intent, disabled replay, crash at verified/prepared/moved boundaries, cancellation before/during hashing, a 32 MiB held-hash response under 250 ms, injected read denial/storage I/O failure, and retry after a simulated storage failure before the verification checkpoint.
- Git diff check passed; Git emitted line-ending notices only.

## Interactive UI check for the user

On 2026-10-15 the Computer Use helper failed before window enumeration ("failed to write kernel assets: The system cannot find the path specified"), including after a reset. A read-only process check found two running pre-M2 copies: artifacts/m1-ui-qa-20261015/payload/AriaGui.App.exe (08:21 build) and artifacts/exe/AriaGui.App.exe (11 October build). The first M2 x64 Debug path given to the user was a packaged build output and could not be launched directly; Windows recorded REGDB_E_CLASSNOTREG for that attempt. A fresh supported unpackaged Debug build succeeded with zero warnings/errors at src/AriaGui.App/bin/x64/Debug/unpackaged/net10.0-windows10.0.26100.0/win-x64. Its EXE, PRI and AddDownloadDialog.xbf are present. That EXE was launched and confirmed as the sole AriaGui.App.exe process with a responding AriaGUI window (PID 25500). The older copies cannot show the new field; use this unpackaged M2 window for the manual check.

User observation on 2026-10-15: the unpackaged M2 Add dialog accepted a supplied SHA-256 and the resulting download reported a checksum mismatch. This passes the manual mismatch-display check only. The supplied value and downloaded bytes were not independently compared here; correct-digest completion, invalid-format rejection, and the other visual checks below remain unconfirmed.

1. In Add download, confirm the optional **Expected SHA-256** field remains usable at normal and larger text/window settings. Invalid input should show an error without creating a download.
2. Start a controlled download with its correct SHA-256. Observe **Verifying SHA-256…** before **Completed**, then select the row and confirm **Matches supplied SHA-256**. The completion notice should say it matched the supplied digest.
3. Repeat with an incorrect 64-hex digest. The row should fail with **SHA-256 mismatch**, retain its prior public file if one exists, and offer Retry. A no-digest download and migrated history should show **Not supplied**.
4. If testing the disabled option, reopen a pending required-digest job and confirm it stays held with a clear reason rather than resuming or appearing completed.

Use a disposable public fixture for this check. Record app build, display/text scaling, and pass/fail notes or screenshots in this file.

## Boundaries

The disk-full path is exercised with injected IOException before the durable verification checkpoint; a physically full volume was not created. The responsiveness test measures ledger observation under a held 32 MiB hash, while interactive UI smoothness needs the manual check above. No M2 commit or package publication has been made.
