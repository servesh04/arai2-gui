# AriaGUI 0.7.0 preview candidate

Prepared 6 October 2026. Local test distribution only; this is not a completed public release.

## Local EXE responsive refresh,11 October 2026

Published `artifacts/exe/AriaGui.App.exe` as an unpackaged self-contained local preview; no new MSIX/external release. Shared Fit/Manual columns, adaptive shell/panes/dialogs, work-area clamp, saved-width normalization and keyboard-grip accessible dimensions are included. 184/184 tests and isolated optimized Release UI/Exit smoke pass; user reports 150%/200% scaling passes. Prior folder preserved at `artifacts/exe-before-responsive-20261011`. Exit old test fixtures before launch because they share the RPC port. Narrator speech, mixed-DPI and clean-machine installer conditions remain untested; see R4_VERIFICATION.md.

## Included

- HTTP/HTTPS downloads through pinned aria2 1.37.0, queue control, pause/resume, retries, durable history, and interruption recovery.
- Search/status filters, 50-record history pages, theme/default-folder preferences, and Open file/Open folder.
- A Windows 11 x64 MSIX containing .NET 10.0.9 and the Windows App SDK runtime. Development SDKs and a separate aria2 installation are not intended prerequisites.
- Protected per-user data, verified backups before schema upgrades, and rejection of schemas written by newer versions.
- Reproducible package-content checks and file-only development signing. A development signature needs explicit trust on the chosen test machine.

## Supported target and current evidence

Windows 11 x64, build 22000 or later is the declared target. Only development-machine builds and automated isolated tests have been exercised so far. Other Windows builds, ARM64, and clean-machine installation are not certified.

## Known limitations / release gates

- Clean-machine install/launch/download/pause/resume/recovery/upgrade/uninstall acceptance remains pending.
- Native keyboard, accessibility, scrolling, tray, sleep/resume, and actual disk-full checks remain pending.
- This engine's Windows build documents no TLS 1.3 support. HTTPS compatibility depends on the server; the GUI currently targets single-file HTTP/HTTPS downloads.
- Signing uses a development publisher. A public publisher, production certificate/timestamp policy, and full third-party redistribution/source deliverables are not yet selected.
- The package has separate LocalState from an unpackaged preview. It does not import the preview automatically. Do not run both concurrently: they use the same loopback RPC port.
- Exit before installing an upgrade or uninstalling. Windows uninstall removes package-managed data, including its history/settings/session backups. Completed and partial downloads outside the package data folder remain on disk. Export private data before uninstall if recovery is needed.

See [Phase 7](PHASE_7.md) for the learning guide and [release acceptance](RELEASE_ACCEPTANCE.md) for the pending matrix.
