# Native EXE smoke test — 8 October 2026

Tested the self-contained, unpackaged Release app at `artifacts/exe/AriaGui.App.exe` on the development Windows host using the computer-use skill. This is development-host evidence; MSIX installation and clean-machine acceptance remain pending.

## Startup fix

The original published EXE exited before opening a window. Windows Application Error recorded `Microsoft.UI.Xaml.dll` with exception code `0xc000027b`. The publish folder omitted `AriaGui.App.pri` and the compiled `.xbf` pages, although the build folder contained them.

`PublishUnpackagedXamlResources` in the app project now explicitly publishes the resource index and compiled pages, preserving their relative paths. The republish succeeded without warnings or errors. Native launch subsequently opened the app and reported Ready with aria2 1.37.0.

Tested artifact fingerprints (SHA-256):

| File | SHA-256 |
| --- | --- |
| AriaGui.App.exe | 26EFD35520BC1B50CCA073491C41BB658DC11878A08ED97D3E2A512E68F42C84 |
| AriaGui.App.dll | E4386B138F279103B32A0C1360A89803C331126BC304112775E6AE9112A79E4A |
| AriaGui.App.pri | B5CA5CCD9998B433F12AC2A06295ACE0F4F19796A47647E0C348D52E350A439A |

## Results

A loopback-only HTTP server supplied deterministic bytes, HTTP Range support, a throttled 16 MiB transfer, and a deliberate HTTP 404. Test downloads went into `artifacts/native-smoke/20261008-retry/downloads`. Existing files and older history were preserved.

| Check | Observed result |
| --- | --- |
| Startup / engine | Passed: published EXE opens and engine becomes Ready |
| Small download | Passed: 262,144 bytes; SHA-256 `06E887C79125F31B04EF2FA098DFFD566CBE4CD34281771901FEBBB07E63817A` matches source |
| Pause | Passed: first transfer stayed at 50% across subsequent UI checks; speed zero and no active downloads |
| Paused restart, repeated test | Passed: `slow-restart.bin` was restored Paused after Exit/reopen; engine reported one known transfer and no unknown entries |
| Resume / byte integrity | Passed: resumed the partial transfer; completed 16,777,216 bytes; SHA-256 `4E95DC3C24E3D83F0224D95C4D2BFFE8797BB6B35009F483F592055584936939` matches source |
| Completed history, repeated test | Passed: `persist-check.bin` persisted alongside the two older completed records after restart |
| Preferences | Passed: saved test folder reloaded and was applied to new downloads; original empty/default folder restored afterward; theme unchanged |
| Ctrl+F / filename search | Passed: shortcut focuses SearchBox; searching `persist-check` leaves its matching row |
| Saved history search | Passed: history shows Page 1, one matching saved record; Previous/Next disabled with only one page |
| HTTP 404 | Passed: `missing.bin` shows Failed, "The server did not find this file.", and Retry |
| Close while downloading | Passed: window disappears while app and owned engine remain alive; transfer progresses |
| Relaunch while hidden | Passed: restores the same window/process and engine, without duplicates |
| Explicit Exit | Passed: app and owned engine stop; completed files retained |

## Findings and limits

**Initial history discrepancy remains unresolved.** The first attached instance began with zero records. After adding `small.bin` and `slow.bin`, saving preferences, and exiting, the next launch showed the two older records instead of those additions. Preferences survived. A second test created `persist-check.bin` and `slow-restart.bin` in the launched instance; both were verified in the real SQLite database and restored correctly on the next launch. This repeat success does not explain or dismiss the initial discrepancy. Keep persistence acceptance open until the initial launch/state context is identified and the restart sequence is repeated from a known clean state. The original completed/partial files remain on disk.

A paused transfer restored by aria2 reports 0 B / unknown until resumed. Resume restores its known size and partial progress, and the final hash proved that the resumed file was correct. Consider retaining clearly labeled last-known progress while awaiting fresh engine metadata.

The captured window was approximately 963 × 765 logical pixels. With the add form expanded, the list has little usable space. Collapsing the form and scrolling the list exposes row actions; a completion notice further reduces the viewport. A responsive layout with a bounded/scrollable form or compact download rows would improve use on smaller screens.

Computer Use occasionally lost cached input geometry. Fresh screenshot-based capture allowed testing to continue. No app crash was observed after the resource publish fix. This smoke test does not validate screen-reader operation, every status filter, multi-page or large-history native performance, direct tray context-menu actions, sleep/wake, disk-full recovery, HTTPS, or installation/upgrade/uninstall on a clean machine.

The test server was stopped and the app exited gracefully. Test history and downloaded files were kept. No production code was changed during this retry turn; the publish fix was made immediately before it. Existing automated test totals in Phase 7 were not rerun for this UI smoke test.
