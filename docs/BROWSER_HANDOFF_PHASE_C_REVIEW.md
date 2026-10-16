# Phase C open-only activation review

Reviewed 2026-10-13 against baseline `9a0d059` and the uncommitted Phase C diff. Scope: application activation, controller, Win32 wake receiver, package manifest, registration tooling, fixtures and recorded evidence. No production code was modified, handler installed, personal profile opened, or commit created by this review. Companion: [Sol correction handoff](BROWSER_HANDOFF_PHASE_C_SOL_HANDOFF.md).

## Decision

The open-only scope and retention of the existing GUI lease are appropriate. Fix the native-host launch regression before accepting the code. Keep deployment acceptance pending until canonical packaged and unpackaged Windows activation has been exercised. The present fixtures establish useful behavior, but do not establish installed protocol handling, real Explorer tray restore, or actual activation during shutdown.

No download payload, credential forwarding or second engine-ownership mechanism was introduced. I found no demonstrated arbitrary-command or file-write vulnerability in the open URI path. The lexical parser is appropriately small; do not broaden it into URL-add support or AppInstance ownership migration as a review fix.

## Findings

### C-R1 — P1: new launch filter rejects the native host's required wake argument

**Location:** `src/AriaGui.App/App.xaml.cs:140-141`; `src/AriaGui.Application/Engine/OpenProtocolActivation.cs:9-10`. Related contract: `src/AriaGui.NativeHost/InstallationDescriptor.cs:45-47`, dispatch in `NativeHostSession.cs:103`.

**Observed source contradiction:** The app now accepts a Launch only with no arguments or exactly `ariagui://open`. The native host's installed descriptor validation requires exactly `--browser-wakeup`, and its launcher passes those configured arguments. Before this diff, `OnLaunched` did not reject that argument. With the new code a host cold launch exits at `App.xaml.cs:48` before runtime/DI/bridge startup; a host-launched secondary also exits before signaling the existing owner.

**Reproduction by control flow:** Pass `["--browser-wakeup"]` to the new Launch classifier: `IsPlainLaunch` is false and `IsOpenLaunch` is false. This is source-proven, not a measured installed browser failure. The production browser listener is still gated, which limits current user exposure, but does not make incompatible component contracts acceptable.

**Fix:** Classify internal app launch commands separately from untrusted protocol syntax. Preserve exactly the existing payload-free `--browser-wakeup` shape; reject added arguments and malformed variants. Keep the protocol branch restricted to open-only. Do not admit arbitrary switches or transplant EngineFixture-only `--bridge-auto-host` arguments into the production GUI.

**Required test:** Unit tests for the shared launch classifier plus a marked synthetic GUI cold and warm launch with `--browser-wakeup`. Add a descriptor-to-GUI compatibility assertion so native-host process tests cannot pass by launching only the separate EngineFixture executable.

### C-R2 — P2: registry dispatch fixture counts any prompt process exit as success

**Location:** `scripts/Test-OpenProtocolRegistration.ps1:32-39`.

**Observed:** The fixture checks only that ShellExecute returns a Process and that it exits within ten seconds. It does not check exit code, executable identity, delivered arguments, or a parser-rejection result. A renamed executable that exits with a failure code, a missing-runtime crash, or an app startup exception can satisfy the current assertion. The registration helper accepts any existing file named `AriaGui.App.exe`, so the fixture does not itself prove that the child is the reviewed application.

**Impact:** The existing report proves registry structure/readback and that some returned child terminates. Its stronger statement that the app rejected the synthetic URI before runtime initialization is not established by this assertion. This does not invalidate the separate successful GUI fixtures.

**Fix:** Use a tiny owned argument-capture fixture for shell command/quoting tests, emitting a fixed structured receipt with the exact synthetic argument vector and exit status. Keep app rejection tests separate and restricted to a verified synthetic profile. At minimum reject nonzero exits and record binary hash, but exit zero alone still does not prove argument delivery or absence of runtime access. Do not make a production log contain arbitrary URI strings to support a test.

**Required test:** A deliberately failing child must fail the fixture. Exercise quoted executable paths and malformed/quoted URI payloads through ShellExecute, not merely direct process arguments. Record command-delivery evidence separately from app acceptance.

### C-R3 — P2: GUI fixture swallows a real shutdown failure and can pass malformed activation without proving rejection

**Location:** `scripts/Test-OpenActivationGui.ps1:69-70,87-103`.

**Observed:** The File/Exit block catches every failure, including the assertion that an invoked exit completes, records `ClosingLimitation`, then exits the script successfully after killing the process in cleanup. This conflates “UI Automation unavailable” with “the actual app failed to exit.” The malformed warm case checks only that the original process remains alive. A broken filter that accepts and opens the original window also meets that condition.

**Impact:** Future runs can be green after a shutdown regression or malformed-input admission. The inspected guarded run does record `ActualExitUiVerified=true`; this finding concerns the test's false-pass path, not a claim that the recorded File/Exit run failed.

**Fix:** Distinguish fixture capability discovery from assertions. If UI Automation cannot locate the owned menu, emit an explicit incomplete/skipped result. Once invocation succeeds, timeout or failed exit is a test failure. The report needs an overall pass/incomplete/fail outcome. For malformed warm input, minimize/hide the owner first and assert it remains unchanged; add malformed cold input in a fresh marked profile and verify no window/runtime mutation. Compare relevant fixture state before/after rather than relying solely on process liveness.

**Required test:** Inject an exit failure and an intentionally permissive classifier; the corresponding checks must fail. Required release mode must not allow an incomplete tray/shutdown result to look like acceptance.

## Required release gates, distinct from demonstrated code defects

### C-G1 — P1 release gate: actual canonical Windows activation remains untested

**Locations:** `App.xaml.cs:135-147`, `Package.appxmanifest:48-52`, evidence's remaining acceptance section.

The actual GUI test uses direct `Start-Process ... -ArgumentList 'ariagui://open'`, which exercises the unpackaged Launch branch. The UUID scheme ShellExecute case is deliberately rejected. Neither exercises a successfully installed canonical `ariagui` handler or the packaged `ExtendedActivationKind.Protocol` branch.

`protocol.Uri.OriginalString` is a sensible choice; there is no demonstrated normalization bypass here. Microsoft documents separate packaged/unpackaged activation paths, and the current C#/WinRT projection source reads native `RawUri` when constructing `System.Uri`. This supports the choice but is not evidence that the exact shipped OS/SDK/browser path preserves every original input byte. [Microsoft activation guidance](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-rich-activation), [C#/WinRT URI projection](https://github.com/microsoft/CsWinRT/blob/master/src/WinRT.Runtime/Projections/Uri.cs).

Run canonical shell and browser activation in an isolated Windows environment for both installation models. Include mixed case, trailing slash, query/fragment, percent encoding, whitespace, quotes, extra arguments and invalid URI forms. Assert correct branch/outcome and no runtime mutation for rejected cold input. Check plain packaged Start-menu launch too. If Windows/browser normalizes input before delivery, state the guarantee as exact validation of the delivered activation value; do not promise inspection before all OS/browser normalization. Do not widen accepted syntax without evidence that valid canonical launches need it.

### C-G2 — P2 release gate: tray, shutdown and engine ownership evidence is incomplete

The guarded GUI report `artifacts/phase-c/gui-67b3e24143414787baf44620be89f84d/gui-evidence.json` records hidden-state injection, `ActualCloseToTray=false`, `ClosingRace="domain fixture only"`, and `ForegroundAfterWarm=false`. These limitations are honestly disclosed. The report has no engine executable, so it cannot demonstrate one real engine under a protocol burst or teardown race. Existing infrastructure ownership tests exercise useful lower-level invariants, not the new end-to-end activation path.

Run on an isolated desktop with Explorer tray: actual Close → tray → URI restore; Explorer restart and tray failure; canonical activation while shutdown holds the lease; and deliberate relaunch after shutdown. In a synthetic runtime with the pinned engine, verify one GUI/engine and unchanged pending-job recovery under bursts and exit. A closing request may be ignored as documented; it must not launch a competing engine. Foreground refusal is allowed, but visible/restored window and usable taskbar/tray fallback must be verified.

### C-G3 — P2 release gate: standalone registry helper is not installer/update acceptance

**Location:** `scripts/Register-OpenProtocol.ps1:13-57`.

The explicit per-user, dry-run default and exact ownership/type checks are sound. No UserChoice overwrite is attempted. Raw registry registration is an available unpackaged strategy; it is not necessary to migrate ownership to AppInstance to use it. [Microsoft registration choices](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-rich-activation).

However, the helper does not prove stable install path, packaged/unpackaged default selection, upgrade/rollback or repair. It intentionally refuses a changed owner path, and multi-value registry updates are not transactional. Read-then-delete ownership checks also do not provide compare-and-delete against concurrent writers. These are already acknowledged operational limits, not a demonstrated cross-user vulnerability.

Before shipping registration, choose one installer owner and stable path policy, serialize that product's installer changes, retain a recoverable update/repair record, and verify unrelated registrations are preserved. Test old-path update, interrupted write, changed command/value type, foreign handler, missing target, unregister after removal, both package models installed, default-handler choice, rollback and clean uninstall. Avoid adding a generic registry framework; keep this single open-only association small.

## Architecture and maintenance observations

* Keeping `SingleInstanceLease` as sole GUI ownership authority is correct. Secondary exits before DI; the engine lease is unchanged. AppInstance is used for reading/events only, so this is not an incomplete ownership migration.
* The new receiver being independent of tray registration removes a real availability dependency. Runtime-scoped wake avoids the old user-only cross-profile broadcast. However, `SetProp` publication is a routing hint, not authenticated owner proof: another same-user process can publish the same public property. Describe the PID as a matching scoped window PID, not a cryptographically verified lease owner. Payload-free open and the same-user threat model make this a documentation/hardening issue, not a credential boundary.
* The receiver has no ACK. If the primary holds its lease but has not published a window, broadcast can be lost; the cold primary later activates its own window. If it is closing, the signal is intentionally ignored. Keep these best-effort semantics rather than adding a persistent activation queue for open-only.
* `OpenActivationController` bounds queued callbacks, not OS process creation or window-message queue length. The 500 ms throttle cannot prevent a webpage/shell from spawning many launchers. Avoid advertising a complete launch-flood defense.
* The controller uses monotonic timestamps and coalesces pending work without extending its deadline. Its real host show action goes through `DesktopLifetimeController`, preserving the closing check. No change to its state machine is needed merely for style.
* `AppInstance.GetCurrent/GetActivatedEventArgs` are newly on every startup and can throw before runtime initialization. Consider a narrow safe failure path with no payload logging, and verify the selected SDK/packaged plain-launch path. Do not catch everything and silently admit unknown activation.
* The initial activation creates/activates the window through ordinary startup rather than `RequestOpen`; the controller's startup latch mainly covers later arrivals. Explain this when describing cold readiness tests; the mocked controller test is not App initialization-order proof.
* Prefer one explicit launch-classification result (`Plain`, `BrowserWake`, `OpenUri`, `Rejected`) used by app/tests. Keep OS activation adaptation separate from this pure classifier. This fixes C-R1 and makes the required compatibility matrix visible without building a generic dispatcher.
* The source manifest declares only a scheme; it cannot restrict the scheme to `open`. `Test-Package.ps1` proves one `ariagui` declaration, while the app parser enforces the action. Adjust its error wording if necessary to avoid implying manifest-level action enforcement.

## Verification performed during this review

Read the complete tracked Phase C diff and new files, related host launch contract, GUI/engine ownership and desktop-show code. Inspected the recorded application TRX (122 executed/passed, zero failures), registration evidence and guarded GUI evidence. PowerShell parser checks passed for all five affected scripts; `git diff --check` passed with existing LF/CRLF warnings. Consulted official Microsoft activation guidance and URI projection source for the Windows-boundary assessment.

No fresh GUI/registry/engine run, package installation or test-suite run was performed. Existing fixture counts are recorded evidence, not freshly reproduced results. The known code regression follows directly from incompatible current caller/callee source contracts; no speculative normalization exploit or engine duplication is claimed.

## Suggested disposition

Fix C-R1–C-R3, add the focused integration tests, then review the resulting diff. Preserve the evidence document's separation of local implementation from deployment acceptance. Complete C-G1–C-G3 in an isolated supported Windows installation before release. Phase A remains deferred and Phase B direct credential forwarding remains blocked; neither should expand as part of these corrections.
