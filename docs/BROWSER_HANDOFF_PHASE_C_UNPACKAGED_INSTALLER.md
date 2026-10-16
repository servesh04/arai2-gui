# Open-only unpackaged installer review candidate

The installer owner is `Install-OpenProtocol.ps1` with `OpenProtocolInstaller.psm1`. It owns only `%LOCALAPPDATA%\AriaGUI\OpenOnly`, with the immutable launch path `app\AriaGui.App.exe`, and the exact per-user `ariagui` association for that executable. This is an installable review candidate; release/default-choice acceptance remains pending. MSIX and the browser companion are separate owners. There are no machine-wide writes, startup launch, browser permissions or app-data migration. No Start-menu shortcut is created by this association-focused candidate; shipping shortcut/bootstrapper distribution remains a separate release packaging decision.

From the prepared candidate directory, inspect without applying:

```powershell
./tools/Install-OpenProtocol.ps1 -Action install -SourceDirectory ./payload
./tools/Install-OpenProtocol.ps1 -Action readback
```

On an **owned isolated test desktop**, append `-Apply` to install. `update` requires the next complete source payload; `repair` accepts only the existing inventory and restores missing files, refusing changed/unknown content. `rollback` selects the one retained previous usable payload without changing the executable path or installation ID. `uninstall` removes only inventoried payloads and the exact owned association, including after the executable is missing. Normal application history/runtime outside this installer root is preserved. An empty installer root/lock may remain for serialization. The installer never launches a GUI or engine.

## Transaction and recovery policy

An exclusive `installer.lock` serializes cooperating installer processes. A same-directory atomic metadata replacement writes a bounded journal before staging or registry mutation, with file `Flush(true)`. The journal binds user SID, root, scheme, installation ID, operation and before/after inventories. A source is copied into `stage`, verified, and switched through `retiring`; the stable `app` directory receives the new version. Updates/rollback retain the old complete version in `previous`. Repair discards a damaged retired copy instead of advertising it as a rollback candidate.

Interruption is surfaced and leaves the journal pending. Ordinary operations refuse pending state. `-Action recover -Apply` explicitly resumes the recorded operation **forward**, verifies compatible partial payload/registry state, commits receipts, and clears the journal. A desired rollback is a separate transaction after recovery. Retain the source bundle until commit/recovery completes; a changed or unavailable recovery source fails closed. There is no all-resource atomicity or tested power-loss guarantee. Process exit without `finally` is tested. Partial deletion accepts only a subset of known inventory with unchanged hashes, never unknown files. All recursive mutation paths are resolved under the exact root and reject reparse paths.

The exact protocol title, owner path, value types, hierarchy and quoted command match the existing registration helper's contract. The journal permits recovery only from compatible subsets of that exact state; changed/foreign values or subkeys stop recovery. The exclusive lock does not serialize another program writing the registry, and read/modify is not compare-and-swap against a malicious same-user writer. Ownership receipts/inventories detect changes; they are not trusted publisher signatures. Source selection is explicit, and shipping binary provenance/signing remains a release gate.

The candidate refuses HKLM handlers and existing packaged canonical handlers rather than shadowing them. It never writes UserChoice or forces a default. Readback reports a preserved existing choice or no selected choice, never verified default selection. Actual packaged/unpackaged coexistence and Windows selection UI must be tested separately. Active installed processes block changes; inspection is best effort plus Windows file locks, not a forced-stop/checkpoint hook.

## Synthetic verification

`Test-OpenProtocolInstaller.ps1` uses only fresh workspace UUID roots under `artifacts/open-installer-tests` and `ariagui-open-fixture-UUID` schemes. Fault modes are rejected without synthetic scope. It covers staged lifecycle, changed-root/foreign/type/content refusals, preserved synthetic UserChoice, missing target, rollback, interruption checkpoints, separate-process lock contention, active owned process refusal and abrupt installer exit. Its package-conflict sentinel exercises refusal policy, not installed package discovery. Exact UUID test defaults are fixture-only and cleaned separately; installer code never writes them. No canonical registration, real profile or existing listener is used.

Reproduce after building `tests/AriaGui.OpenActivationFixture`:

```powershell
./scripts/Test-OpenProtocolInstaller.ps1
```

For release acceptance, run this candidate in the owned VM, install the separately signed MSIX, exercise real default conflict/selection and installer update/repair/rollback/uninstall with recorded hashes, then verify owned package/association/process cleanup. Follow the [isolated runbook](BROWSER_HANDOFF_PHASE_C_ISOLATED_RUNBOOK.md). Passing synthetic recovery does not pass installed C-G3 or clean-machine acceptance.
