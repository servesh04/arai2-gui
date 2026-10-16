# Phase C correction handoff for GPT-6.1 Sol

Baseline `9a0d059`, uncommitted open-only slice reviewed 2026-10-13. Read [Phase C review](BROWSER_HANDOFF_PHASE_C_REVIEW.md) and [evidence](BROWSER_HANDOFF_PHASE_C_EVIDENCE.md). Preserve existing unrelated work. This handoff originally described future corrections; C-R1–C-R3 are now implemented and tested. Read the current evidence for measured local results and remaining C-G1–C-G3 deployment gates. The review itself changed documentation only.

## 1. Fix launch compatibility first

Extract or extend a pure launch classifier so the app admits exactly: no arguments, the existing payload-free `--browser-wakeup`, or one exact case-insensitive `ariagui://open`. Keep OS Protocol activation on the strict open-only parser. Extra arguments, unknown switches and malformed URI inputs remain rejected. Do not add EngineFixture's `--bridge-auto-host` command to the production app.

Update `App.xaml.cs` to use that classifier. Add unit cases and marked synthetic GUI cold/warm `--browser-wakeup` checks. Verify the installed native-host descriptor's required arguments against the same contract; current host tests launching EngineFixture do not catch this regression. Keep browser feature gates unchanged.

## 2. Make fixture failures meaningful

For `Test-OpenProtocolRegistration.ps1`, split registry command/quoting proof from actual app rejection. Use an owned tiny argument-capture executable with a structured receipt for the synthetic scheme. A nonzero/crashed child must fail; record exact delivered argument count/value for synthetic data and binary hash. No arbitrary URI logging in production.

For `Test-OpenActivationGui.ps1`, discover UI Automation capability separately from invoking Exit. Once invocation succeeds, a shutdown timeout must throw; cleanup can still terminate owned synthetic processes. Report pass/incomplete/fail explicitly, and make required acceptance mode reject incomplete results. Minimize/hide the owner before malformed warm launch and assert no restore; add malformed cold launch with no window/runtime mutation. An intentionally permissive parser and an intentionally non-exiting fixture must make these tests fail.

Do not install canonical handlers or use personal profiles for routine development checks. Retain the existing verification-build guard and owned-resource cleanup.

## 3. Close actual Windows acceptance separately

In an isolated supported Windows environment, deploy the signed/test-signed package and the intended unpackaged installer. Exercise canonical `ariagui://open` through shell and browser, plus malformed variants and plain Start-menu launch. Observe packaged Protocol versus unpackaged Launch behavior with safe reason codes, not payload logs. Record OS/SDK/browser, binary/package hashes and exact outcomes.

Test real tray close/restore, Explorer loss, foreground refusal fallback, and activation during actual shutdown. Add a synthetic pinned-engine scenario to prove one GUI/engine and preserved recovery under bursts and teardown. Keeping ignored opens during shutdown is acceptable if deliberate launch after shutdown works and the documented lease behavior holds.

Complete stable-path registration ownership/update/repair/rollback/uninstall and default-handler conflict cases. Keep UserChoice untouched and preserve foreign keys. Partial registration writes must yield a recoverable documented state; do not claim installer acceptance from standalone readback.

## 4. Keep the architecture small

Retain the existing GUI lease and engine ownership. Keep payload-free Win32 wake and its best-effort no-ACK semantics. Do not introduce AppInstance ownership migration, URL-add activation, credentials or a persistent activation queue. Document scoped HWND publication as routing rather than authentication; foreground permission remains best effort.

Update canonical docs/evidence with the fixed launch shapes, actual measured acceptance and remaining gates. Narrow “before normalization” to the actual delivered-value boundary unless installed Windows/browser tests establish the stronger claim. A manifest check proves scheme registration; the app enforces open-only action.

## Exit criteria

C-R1–C-R3 from the review are fixed and focused regression checks pass. Deployment acceptance remains explicitly pending until C-G1–C-G3 have recorded evidence. Do not silently turn an unavailable test environment or zero process exit into a passed feature gate. Report changed files, validation and remaining limitations; no Phase A/B scope expansion.


## Latest feasible C-G2 completion

The marked private-port pinned-engine fixture now passes burst, actual shutdown and guarded paused pending-job recovery without using the existing 16800 listener. Normal Release app composition excludes the override. See current evidence for exact artifacts and hashes. Remaining work is isolated installed-package/browser activation, Explorer loss, and full stable-path installer/update/repair/default-handler acceptance; C-G1/C-G3 remain pending. No commit or Phase A/B expansion.

## Remaining-gate preparation

The next continuation inventoried isolation/signing: Sandbox executable absent; Hyper-V enumeration denied by Windows authorization policy; no owned remote target. Existing file-only signing produced an inspected development-signed candidate without host trust or install. An offline Sandbox bundle and guarded readiness script are prepared; host-refusal/mapping/certificate-absence checks pass. See [isolated runbook](BROWSER_HANDOFF_PHASE_C_ISOLATED_RUNBOOK.md) and current evidence for artifacts. Guest execution and installed/browser/Explorer/clean-machine results remain pending. C-G3 also needs a shipping unpackaged installer owner and serialized repair/update policy; do not invent a passed installer lifecycle from helper tests. No commit made.

## C-G3 installable candidate

The later continuation supplied `Install-OpenProtocol.ps1`/`OpenProtocolInstaller.psm1` as the narrow per-user owner with fixed OpenOnly/app path, exclusive installer lock, inventoried staging/previous payloads and a flushed forward-recovery journal. Synthetic lifecycle/process/fault checks and a self-contained Release payload build now pass; see current evidence and [installer policy](BROWSER_HANDOFF_PHASE_C_UNPACKAGED_INSTALLER.md). This supersedes the prior missing-implementation blocker. Actual isolated install/coexistence/default/reboot/clean-machine acceptance remains pending, plus the shipping shortcut/bootstrapper distribution decision. No host canonical handler or app data was changed, no commit.
