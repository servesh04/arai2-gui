# Phase B credential feasibility evidence

Measured 2026-10-13 against baseline `2753957`. **S01 and S02 are blocked. Credential forwarding remains unavailable.** No production DTO, browser permission, preference, RPC option or app recovery behavior was expanded. There is no new flag capable of enabling credentials.

B0 follow-up on the same date: all five fields are now checked automatically at each of seven lifecycle snapshots, in both retained restore copies and restored engine options, and at each of four redirected targets. Changed observations produce `changed_inconclusive` evidence and nonzero exit after writing the report. Current and retained rotated-name logs plus unknown-log exclusion are inspected through the real exporter. Latest evidence: `artifacts/browser-phase-b/b0-final/evidence.json` and `diagnostics-evidence.json`; the original `verified` run below remains historical. Actual engine rotation and production exception-path instrumentation are not claimed. See [A0/B0 corrections](BROWSER_HANDOFF_A0_B0_EVIDENCE.md).

The executable spike is `tests/AriaGui.Browser.Spikes/CredentialSpike.cs`, invoked by `scripts/Test-BrowserCredentialFeasibility.ps1`. It verifies the binary hash before launch, checks the live version through authenticated RPC, creates fresh synthetic markers, and uses controlled loopback servers. It never reads browser profiles, cookies, SQLite history or real secrets. The host and extension are not involved.

## Reproduce

From the repository root with .NET 10, restored locked dependencies and Python 3.8+ (`python` on PATH):

```powershell
./scripts/Test-BrowserCredentialFeasibility.ps1 -ArtifactDirectory artifacts/browser-phase-b/new-run
```

Use a fresh empty directory. The synthetic-only experiment requires local process and loopback connectivity; sandbox execution could start aria2 but could not reach RPC, so the recorded run used an approved unsandboxed invocation. Exit zero means the experiment completed and recorded its blockers; it does **not** mean S01/S02 passed. An unexpected absence of the measured session/cross-origin cookie leaks fails the experiment and requires reviewing changed engine behavior.

Recorded run: `artifacts/browser-phase-b/verified/evidence.json`; actual exporter inspection: `artifacts/browser-phase-b/verified/diagnostics-evidence.json`. Artifacts are ignored, local evidence. Reports contain field labels, relative artifact paths, byte counts and SHA-256 hashes, never marker values. Raw session/log artifacts intentionally retain unique synthetic values for independent inspection. The harness also leaves only its synthetic TLS certificate/key under the artifact directory; it installs no certificate or key in Windows stores. The Python TLS tunnel stores no traffic and is terminated at cleanup.

Pinned engine: aria2 **1.37.0**, executable SHA-256 **BE2099C214F63A3CB4954B09A0BECD6E2E34660B886D4C898D260FEBFE9D70C2**. Engine configuration follows relevant app settings (`no-netrc`, authenticated loopback RPC, five-second auto-save and session-save intervals), with verbose engine logging for inspection, fixture timeouts and proxy disablement. Certificate checking is disabled globally **only in this synthetic engine** to allow the local self-signed TLS server; app certificate checking remains unchanged. TLS 1.2 completes before the downgrade redirect.

## S01: persistence

Each request carries five independent markers: Cookie, Authorization, custom header, User-Agent and referrer. The snapshot scanner reads every file under the fixture, including session/restore copies, config, engine debug log, output/control files and synthetic TLS files.

| Boundary | Measured result |
|---|---|
| Paused add + explicit `aria2.saveSession` | All five fields present in plaintext `lifecycle/session.txt` before any origin request. |
| Autonomous periodic save | Delete the first snapshot and wait for the five-second timer to recreate it; all five fields present. |
| Active transfer → forcePause → checkpoint | All five fields remain in session. |
| Graceful engine shutdown | All five fields remain in session. |
| Restore from a retained session copy | Exactly one suspended transfer restored; `aria2.getOption` returns all five marker fields, and save-session serializes them again. |
| Resume → checkpoint → forced process kill | Saved session survives forced termination with credentials. |
| New engine restored from crash checkpoint | Exactly one suspended transfer restored; new checkpoint still contains all five fields. |

The normal-save session is 536 bytes in the recorded run, SHA-256 `82ACA2C8E29DA5610AB6ADD441F80AA711B16F9D272BD241DF07893D77B34F55`. The report records exact hashes at every scan boundary. Session `restore.txt` copies also contain plaintext credentials. Config files contain none of the five **download** markers; the separately generated RPC token is intentionally config-local and is not one of these credential markers.

Debug engine logs after network activity contain the custom-header, User-Agent and referrer markers; Cookie/Authorization were not observed in those debug logs. Running the actual `Export-Diagnostics.ps1` on the final fixture leaves custom-header and User-Agent markers in lifecycle, recovery and redirect log exports. URL redaction removes the referrer marker. This demonstrates that the exporter is not a general richer-header scrubber. Production uses app-owned safe diagnostics and does not enable this engine debug log, so this is not evidence that today's public-GET feature logs cookies.

SQLite/WAL, real app backup paths and application restart/suspend coordination are **not exercised**: the production contract does not accept credentials, and no app credential path was introduced. Session restore copies model retention, not the application's full recovery planner. These scopes cannot be marked passed. The engine's serialization is already sufficient to block a direct-header implementation before introducing secret storage anywhere else.

## S02: redirect scope

The source and target fixture listeners capture only route/server labels and which unique markers arrived. Four complete redirected downloads each write the expected synthetic four-byte output. Every redirect target receives **all five fields**, including Cookie and Authorization:

| Redirect | Target observation |
|---|---|
| Same scheme/host/port → new path | All five fields, expected positive control. |
| HTTP → different port | All five fields at the second origin. |
| HTTP `127.0.0.1` → `localhost` on target port | All five fields at a different hostname/origin. |
| HTTPS local TLS endpoint → HTTP target | All five fields after a verified successful TLS request and downgrade. |

Unique `?case=` routes correlate every target request to its source case; a listener request count alone does not stand in for redirect evidence. These are raw header options, not a correctly scoped browser cookie jar. Store/partition/container evidence, cookie domain/path/secure matching, same-name cookie selection, grant expiry and private browsing have no implementation and remain unsupported/unproven. Existing bounded contract tests continue to reject `cookie` and `headers` fields.

## Decision and validation

Direct credential/header options on the shared persistent engine cannot meet ephemeral credential or redirect-origin requirements. App-only encryption cannot prevent these session writes. Removing a session after a save, or encrypting it afterwards, cannot undo the observed plaintext persistence. Stop Phase B feature expansion here.

A later design must prevent credentials from reaching the persistent engine and enforce redirect scope in the actual transport, or explicitly approve a different isolation/persistence design before implementation. The existing continuity gateway must not be assumed credential-safe without its own lifecycle and redirect evidence.

Validation: spike project builds with zero warnings/errors; final wrapper completes all lifecycle/redirect stages and scans four actual diagnostics exports; 94 .NET browser-contract tests and 23 JavaScript fixture/schema tests pass; `git diff --check` passes. No package, installation, real browser session or credential release acceptance is claimed. Phase B changes are intentionally uncommitted.
