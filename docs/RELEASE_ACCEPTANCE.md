# Phase 7 release acceptance

The local build is a candidate. Keep this matrix pending until the actual checks are performed; a successful build or unit test cannot fill the native installation rows.

The unpackaged EXE was tested on the development host on 8 October 2026. See [native EXE results](NATIVE_EXE_TEST.md) for successful download/pause/resume, preferences, search, close/relaunch, and exit checks, plus an unresolved initial history discrepancy. These results do not replace the installed-MSIX matrix below.

## Test environment

Use a disposable Windows 11 x64 VM or separate test machine with no Visual Studio, .NET SDK/runtime, Windows App Runtime, or separate aria2 installation. Record Windows edition/build, architecture, installed runtimes before installation, package SHA-256, certificate thumbprint, and test date. Take a VM checkpoint before trusting a development certificate. This host's Hyper-V inventory query was denied by Windows authorization; no VM was started.

Only the public .cer belongs on the test machine; never copy the .pfx/private key there or include it in a release. The certificate subject must exactly match the manifest publisher. Trusting a development certificate changes which signed code the test machine accepts, so make that choice explicitly on the disposable machine. A production package needs the chosen publisher's trusted signing path instead.

## First installation on the chosen test VM

Copy the development-signed MSIX, public AriaGUI.Development.cer, signed-package-report.json, and signing-report.json. Compare the MSIX SHA-256 and certificate thumbprint with those reports before changing trust. Take the VM checkpoint first.

If you choose to trust this development publisher on that disposable VM, run the following there in an administrator PowerShell. This is a manual test step; our build/signing scripts never run it:

    Import-Certificate -FilePath .\AriaGUI.Development.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople

Then install from a normal PowerShell session for the test user:

    Add-AppxPackage -Path .\AriaGUI_0.7.0.0_x64_development.msix
    Get-AppxPackage -Name BC1C2CFD-F4A5-45B7-82AD-26C6210B635D |
        Select-Object Name, Version, PackageFamilyName, InstallLocation

Launch AriaGUI Preview from Start and follow the matrix below. If trust/deployment policy blocks installation, retain the error and resolve it on the test machine explicitly; do not weaken the development host's policy to make a build look accepted. Microsoft's [certificate guidance](https://learn.microsoft.com/windows/msix/package/create-certificate-package-signing) describes the required trust store.

After completing the tests, Exit and preserve any needed private state before uninstalling from the test user's session:

    Get-AppxPackage -Name BC1C2CFD-F4A5-45B7-82AD-26C6210B635D | Remove-AppxPackage

Reverting the disposable VM checkpoint also removes the temporary trust decision. Record results before reverting.

## Matrix

| Check | Steps / expected result | Evidence / status |
| --- | --- | --- |
| Package contents | Validate identity/version/OS/x64, runtimes, aria2 hash, notices, and exclusion of private data/test binaries | Passed locally; package-report.json and signed-package-report.json |
| Fresh install | Trust the chosen development certificate on the test VM, install the signed MSIX; no developer tools or engine download | Pending |
| Launch | Start from Windows Start; window/tray available, engine Ready; data created in package LocalState, not installation directory | Pending |
| Real download | Download a known HTTP/HTTPS file, compare SHA-256 with source, test missing URL error | Pending |
| Pause/resume | Throttle a large transfer, Pause, verify no progress, Resume to completion | Pending |
| Exit/reopen | Exit from tray, reopen; paused intent, history, settings and queue limit remain | Pending |
| Crash/recovery | In the isolated VM, interrupt GUI, reopen; no duplicates, known transfers reconnect truthfully | Automated isolated engine regressions pass; installed app pending |
| Native usability | Ctrl+F, filter/history paging, keyboard traversal, screen reader, sustained scroll/type with scale data | Pending |
| Upgrade | Install a lower-version candidate with same Name/Publisher, seed data, Exit, install higher version; preserve IDs, settings, pause/queue intent and files | Schema upgrades tested from Phase 3/4; MSIX version upgrade pending |
| Migration failure | Conflicting schema/access failure; readable failure, original data and verified pre-upgrade backup retained; no silent reset | Passed in automated real SQLite checks |
| Downgrade | Older app must refuse a schema with unknown newer migration IDs; do not force a package downgrade on real data | Automated schema rejection implemented; installed behavior pending |
| Uninstall | Exit, export private data, uninstall; package data removed; external completed/partial files preserved | Pending |
| Reinstall | Reinstall produces fresh package LocalState; external downloaded files remain collision-protected | Pending |
| Diagnostics | Export allowlisted metadata/redacted logs; no config, sessions, database, URLs or RPC credentials copied | Passed in synthetic redaction/tool checks |
| Public signing | Final publisher, trusted signing certificate, RFC 3161 timestamp, chain/signature verification | Pending |
| Redistribution | Complete exact corresponding-source and dependency-license/notice review/delivery | Pending; see THIRD_PARTY.md |

## Upgrade and uninstall rules

Keep package Name and Publisher stable; increase all four version fields according to the chosen release numbering. The current development identity was scaffolded before this phase and remains stable. Do not rename it between versions you want Windows to treat as an upgrade.

Exit before replacing/uninstalling the app so its child engine can checkpoint and stop. A running engine's executable is in the old install location; an upgrade should not depend on that location remaining usable. The app has no installer hook that can promise forced-stop/checkpoint behavior for a package removed while its process is running.

Packaged state: %LOCALAPPDATA%\Packages\<package-family-name>\LocalState\Engine. Preview state: %LOCALAPPDATA%\AriaGUI\Engine. They are deliberately separate; there is no automatic import. Both use port 16800, so run one at a time. Package updates preserve package-managed data; uninstall removes it. Default downloads are in Downloads/AriaGUI, outside LocalState.

If you selected a destination inside package LocalState, those files are subject to Windows package-data removal too. Use an external download folder for files you want to retain after uninstall.

## Private backup and recovery procedure

Before a package upgrade/uninstall, Exit and copy the entire Engine folder to a private backup location. It contains sensitive URLs and engine secrets/session data; never use it as a public bug-report attachment. Copying only downloads.db while the app is running is not a consistent SQLite backup because WAL data may be separate.

Before applying pending schema migrations, the repository creates Backups/downloads-before-upgrade-<timestamp>-<id>.db using SQLite's online backup API and verifies quick_check. Current-schema startup makes no extra backup. This database backup is separate from engine session/control files and completed/partial download data; it does not roll them back.

If a migration fails, Exit, preserve the whole current Engine folder, and inspect the error's backup path. Restore only with the matching older app/schema in an isolated copy first. Replace downloads.db only while every app/engine using that runtime folder is stopped; preserve and remove the old -wal/-shm files from the working recovery folder so they cannot replay against the restored database. Keep original files in the private backup. Never delete completed files to force a history restore.

The app does not automatically roll back a partly migrated database, delete old backups, or reset corrupt history. An older executable rejects unknown newer migration IDs. If no verified pre-upgrade backup was possible, it stops before migrating.
