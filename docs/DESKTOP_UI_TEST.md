# Desktop UI refresh validation

## Published responsive preview,11 October 2026

[Launch EXE](../artifacts/exe/AriaGui.App.exe); keep the entire folder together. User-attested150%/200% manual scaling checks pass;150% Windows text-size/default high-contrast screenshots remain static evidence. Final 184/184serial tests pass; Application 94 reran after the UI-only accessible-grip announcement fix. Clean self-contained normal Release publish and 604-file staging/published verification pass. DLL SHA256 `3CBEF06A696FC8073493FA5E60E74DC9F51647BEBF9A01447C81AD864FF4CC99`. Old folder preserved at `artifacts/exe-before-responsive-20261011`.

Explicitly authorized fresh isolated optimized Release smoke passes launch/search/selected inspector/grip Right 218.2→228.2 announcement/deep-scrollselectionreturn/FileExit. Published apphost/PRI match the smoke copy; its opt-in DLL differs only in private-profile routing. No production profile launch. Engine readiness is untested in this smoke because the old user-controlled fixture occupies port 6800: **File→Exit that old fixture before launching the refreshed EXE.** Narrator speech, final realized-grid ID/count profiling, smaller-monitor reopen and mixed DPI remain known untested. See [R4 summary](R4_VERIFICATION.md)/[appendix](R4_VERIFICATION_APPENDIX.md) for provenance; earlier unpublished checkpoints below are historical.

## Current release decision, 11 October 2026 (round 4)

**Unpublished; manual acceptance incomplete.** [R4 one-page summary](R4_VERIFICATION.md) and [evidence appendix](R4_VERIFICATION_APPENDIX.md) supersede older CPU conclusions below. Direct, untargeted Debug/Release 60-second populated-table controls measured 1.327%/1.535% one-core CPU. Work-area and 60–600DIP load clamps are implemented; native old-width load yielded 2012DIP canvas, 21 aligned rows and unchanged JSON. Sidebar/details arrows updated accessible Names. CacheLength 1 initial realization passed; deep recycling and Narrator speech remain unverified. Serial tests 184/184 pass. Final source has not received a full native/display rerun.

User-labelled [high-contrast](../artifacts/r4-sol/round4/manual-images/user-high-contrast.png) and [larger-text](../artifacts/r4-sol/round4/manual-images/user-larger-text.png) screenshots now provide partial static evidence: shell, rows and footer are readable without visible overlap. User identifies Windows text size 150%; high-contrast default theme/default display scale, numeric scale unknown. Build and interaction checks remain unproven. Display scaling was not tested. User subsequently reports 150%/200% manual display-scale checks “clean and works perfectly” and authorizes local publication; these are user-attested passes. Larger-text/high-contrast detailed interactions, Narrator grip speech and final cache/column-grip checks remain known untested. Clean candidate and backup are ready; isolated smoke-test choice awaits explicit native-input authorization. Primary is not yet replaced. Restore original settings after each display test. Mixed-DPI/R0 causation are known untested limits. Native recovery detected user input, so input stopped and the private fixture/engine remain running; previous Settings normal launch exposed no targetable window. Main DLL and production preferences are unchanged. Historical sections below retain their original build identities and limits.

## Latest resumed R4 checkpoint, 11 October 2026

**Release remains unpublished.** See the latest section in [R4_VERIFICATION.md](R4_VERIFICATION.md); the 10 October table below remains historical evidence.

OS edge snap is now observed at 125%: settled 766.4×878.4DIP client,766.4×630.4canvas, then restore 800×600 with preferred sidebar 160/details 180 preserved; all 94 tracked grids aligned. Restoration used the documented helper activation recovery after a non-target Snap Assist input result. A real localhost mixed Pause batch stayed `IsBatchRunning=true` through 800×600→560×420 (242.4-DIP canvas); Resume stayed true through 1280×800→800×600 (372-DIP canvas). Both selected IDs and alignment survived; actual engine outcomes were1confirmed/1skipped. A disclosed temporary marked-profile-only20-second delegate delay created the native observation window, then was removed. The generated16-MiB file completed with the expected hash and source stopped normally.

New bounded scan counters prove no sustained container/row/check scan work: counts 51/55/151 and scan times unchanged while CPU consumed 20.765625seconds/50.068343wall=41.47%onecore. This closes that source hypothesis, not the native hot-path/performance gate. MatchedR3/static/minimized controls and OS-blockedWPR attribution remain as documented. No speculative performance fix or disabled accessibility was retained.

Both source files exactly match round3 snapshots after diagnostic removal. Forced final unpackaged Rebuild passed 0 errors / 0 warnings (34.43seconds); clean private DLL SHA256 `A45BCB0DF1AB13BE80D66259BF57864C4E2FA42519913EF35735FECCB10ADE01`. Application85/85 reran (15 seconds); previous final-source Core 16/Infrastructure73 still apply to unchanged permanent source. Clean final DLL natively passed560×420, canvas 722.4×242.4, then exited. Earlier seven-dialog evidence remains on identical functional source, not a newly claimed final3 dialog rerun.

Settings retry returned `launched app did not expose a targetable window`; no setting changed or approval bypassed. Other display/text/high-contrast/monitor and small-monitor reopen conditions remain unexecuted, along with exact visible CPU acceptance. All private windows/owned engines exited; localhost source stopped. Production preference/main DLL hashes remain unchanged. Screenshots, geometry, temporary-source provenance and summary are in `artifacts/r4-sol/round3`.

## Responsive R4 follow-up, 10 October 2026

The earlier results below describe the prior primary EXE. Current responsive evidence is in [R4_VERIFICATION.md](R4_VERIFICATION.md), with prerequisites [R1](R1_VERIFICATION.md), [R2](R2_VERIFICATION.md) and [R3](R3_VERIFICATION.md). **R4 release acceptance remains incomplete; the main EXE has not been replaced.**

Final-source suites reran serially and pass 174/174: Core 16 (176 ms), Application 85 (8 seconds), Infrastructure 73 (3 minutes 36 seconds). Latest TRX files are in `artifacts/r4-sol/round2/test-results`; these now follow all guard-only UI edits and removal of temporary diagnostics. Final unpackaged Debug rebuild took 17.59 seconds, zero errors and one cached NU1900 warning, without restore/download. Private live DLL SHA256 is `20716BBFCD1881B550E8DDDF5D32C86C89ED5019D6EAD49033A753A0D3A201A2`; the full dialog inventory used matrix DLL `9A19968FF04A474948F3E0AE34CDD7300D56276C11BD4F6907D6FC5BBE81CAB0` from exactly the same restored DownloadsPage source. Stable apphost SHA256 is `26EFD35520BC1B50CCA073491C41BB658DC11878A08ED97D3E2A512E68F42C84`. These identify private synthetic copies, not a published Release folder.

| Responsive check | Current evidence |
| --- | --- |
| Client sizes/scale |125%:560×420,800×600,963.2×764.8,1280×800,560×800,1280×420,max1536×880. Search value/focus retained across799.2/800/800.8 and1099.2/1100/1100.8 both directions;0.8DIP is the physical-pixel step |
| Table/selection | Max Fit canvas matches1369.6DIP viewport. Recycled selected0400 remains stable. Fresh defaults show six columns; empty, two mixed/unknown-size and100-row cases inspected. Shift+Down/Ctrl+Space and minimum resize preserve intended IDs |
| Customization/persistence | Handled-pointer focus repair enables arrow sizing for sidebar, Name and details. Comfortable39.2→compact31.2DIP; Size hide/Status reorder align. Preferences Save preserves System/empty folder and Manual/pane edits; R3 proves shrink/Exit/reopen/enlarge restoration |
| Extreme saved widths | Final 9A, all columns visible, ordered 1000/1000/60/60/1000/60: minimum Manual canvas 3212×182.4; Fit 722.4×182.4 with intentional minimum-width overflow. Raw preferred widths retained. Max Fit canvas and viewport both 1359.2 DIPs. All 24 tracked minimum grids aligned |
| Shell/dialogs/notices | Final 9A reruns all seven dialogs at 560×420, including settled Batch end, long Preferences end and Details file actions; essential fixed actions reachable. Minimum notices leave 182.4-DIP table. More/footer inspected; focused overlay Saved history survives resize to 800×600 and Space toggles it |
| Real local progress/batches | Final 207: selected generated 16-MiB localhost download survived 800×600→560×420→1280×800 while downloading, stable ID/width alignment. Minimum canvas 242.4 high; wide canvas/viewport 1113.6. Real mixed Pause/Resume each reported 1 confirmed / 1 skipped; subsequent minimum resize kept both IDs and 192-DIP table. File completed with SHA256 `4E95DC3C24E3D83F0224D95C4D2BFFE8797BB6B35009F483F592055584936939`. Batches completed before resize, so in-flight batch timing remains unverified |
| Performance/gates | Repeated live returns sampled 88 tracked grids both times; visible alignment matches, cached unbound parked rows excluded. Controlled CPU: geometry disabled 41.12%, empty results 6.80%, matched R3 49.74%, Progress hidden 37.03%, static single-control rows 32.31% of one core. Minimized final ready-engine fixture 0.84%. Steady pane/layout callbacks only two per ten seconds. Immediate root handling of 680–884 accessibility requests/10 seconds costs only 11–14 ms, so it does not explain the CPU. No concrete source defect/hot function identified; WPR policy still blocks native stacks |
| Remaining native conditions | Maximize/restore passed on 9A; edge drags, native system menu and Maximize hover did not establish OS snap. Settings launch returned `Computer Use app approval timed out`; no settings changed. Other DPI/text/high-contrast/monitor conditions, small-monitor reopen and in-flight batch resize remain unverified. Repeated minimized-window recovery reported user input; native input stopped, leaving private live app/engine running/minimized |

Screenshots, preferences, geometry/checkpoints, process samples and actual snapshot diff live in `artifacts/r4-sol`; the detailed report distinguishes build identities and intermediate frames. Production preference hash remains `5D47898973731ED788EC9EF64194D22C672106BB9D0923962F30CEDCF81D8638`. Only private synthetic profiles were changed. Unexecuted conditions are not passes, and no publication is authorized by these partial results.

`matrix-profile` preferences: compact density, sidebar 170, details 190, Fit mode; all six columns visible with the extreme raw widths above. Latest `live-profile`: compact/Fit, default columns, sidebar 160/details 180, private workspace download folder. Both retain pane preferences through temporary collapse. Display scale measured 125%; Settings access timed out before reversible display/text tests. The localhost-only source stopped normally after completion; the original minimized matrix fixture and all diagnostic windows exited before the latest live session. CPU thread-origin/message timings are not function-level hot-stack samples; the matched R3 comparison does not replace the unavailable historic R0 baseline.

This checklist covers the native desktop refresh. Results must come from the updated build; the previous EXE smoke test is recorded separately in `NATIVE_EXE_TEST.md`.

**Subsequent user report, 9 October 2026:** a maximized screenshot shows lateral table gaps, header/body misalignment and inflexible filter placement. The passes below describe the specific configurations tested, not general resize correctness. The follow-up [responsive UI plan](RESPONSIVE_UI_PLAN.md) reopens table/layout acceptance across window sizes and defines R0–R4 verification. No responsive fixes or new runtime tests were performed in the planning step.

R0 later reproduced the maximized table gap and capped search in the current primary EXE; see [native measurements](R0_NATIVE_MEASUREMENTS.md) and [Astra evaluation](R0_EVALUATION.md). It did not reproduce the reported large header/body mismatch. R0 is partial, and no responsive implementation or replacement EXE has been produced.

## Automated verification

9 October 2026: `powershell -File scripts/Verify.ps1` completed locked restore and the native solution build with zero warnings/errors. All 147 tests passed: Core 16, Application 58, Infrastructure 73. TRX evidence is in `artifacts/test-results`, timestamped 14:48:49, 14:48:52 and 14:49:04. The earlier interrupted infrastructure run timed out in `Crash_before_rpc_preserves_intent_without_auto_add_then_manual_recovery_uses_same_id`; that timeout did not recur in this complete run. Its cause remains unproven.

Release unpackaged publish succeeded to `artifacts/desktop-ui-preview`, then `artifacts/exe`. Native review corrected header alignment and replaced WinUI's inactive Extended-mode checkbox setting with explicit native row checkboxes. A subsequent filter/reuse test caught checkbox state failing to mirror keyboard selection; realized-control discovery on selection and container realization fixed it. Final Release publish and the focused native reproduction passed. These View-only changes did not require repeating the 147 behavioral tests. Both dialog XBFs, DownloadsPage XBF, and the PRI ship in the primary folder.

## Native acceptance matrix

| Area | Check | Result |
| --- | --- | --- |
| Startup | Updated unpackaged EXE loads the table and both native dialogs; owned engine reaches Ready | Passed on preview and final primary EXE |
| Downloads | Add a known loopback payload, observe progress, pause/resume, verify final SHA-256 | Passed: 256 KiB and 16 MiB payloads; hashes below |
| Selection | Ctrl/Shift selection, checkboxes, select visible; progress updates retain selected job IDs | Passed: Shift+Down, Ctrl+Space, independent checkbox add/remove, select all visible, filter-clear reuse and checkbox synchronization; progress retained selected IDs |
| Batches | Mixed eligible/ineligible rows produce accurate per-item totals; command errors remain visible | Pause and Resume each reported 1 confirmed / 1 skipped; per-item dialog correctly named slow and completed rows. Error/race outcomes covered automatically |
| Browsing | Search/filter clear hidden selection safely; saved history sorts before pagination | Ctrl+F, filename search and filter-clear hidden selection passed natively; pagination sort passed automatically |
| Table | Six supported columns resize, reorder, hide/show, and sort by underlying values | Name drag resize, Status reorder, ETA hide and numeric Size ascending passed; corrected alignment verified on final EXE |
| Layout | Sidebar/details resize; compact/comfortable rows and layout restore after restart | Comfortable density, Name width 250.4, reordered/hidden columns restored. Pane resize native checks pending |
| Preferences | Theme/default folder remain intact when changing layout; dialogs stay usable in a small window | Passed at approximately 963 × 765 logical pixels; Windows theme and empty/default folder retained |
| Keyboard | Ctrl+F, Ctrl+N, list navigation, keyboard splitters and accessible names | Ctrl+F and Shift+Down passed; other keyboard acceptance pending |
| Accessibility | Windows animation preference, app animation preference, high contrast and DPI scaling | Pending |
| Lifetime | Close hides; reopen reuses the running instance; File > Exit closes dialogs and stops owned engine | File > Exit stopped app/engine and restored both new completed records on final launch; final app and fixture exited. Close/reopen behavior covered by prior EXE smoke, not repeated |

The loopback fixture is `artifacts/native-smoke/20261009-desktop/server.py`. It writes its dynamic port and expected SHA-256 values to `source.json`; creating `stop.txt` ends the server. Use unique destination filenames so existing downloads are retained.

Native test files were retained in `artifacts/native-smoke/20261009-desktop/downloads`:

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| desktop-sol-small.bin | 262144 | `06E887C79125F31B04EF2FA098DFFD566CBE4CD34281771901FEBBB07E63817A` |
| desktop-sol-slow.bin | 16777216 | `4E95DC3C24E3D83F0224D95C4D2BFFE8797BB6B35009F483F592055584936939` |

The slow transfer paused at 27% with zero speed, then resumed to completion. Five existing records remained intact; final launch showed seven records, six completed and one earlier failed record. This known-instance restart success does not explain the earlier history discrepancy.

Computer Use initially stalled in `sky.launch_app`/window enumeration and was interrupted. A bounded fresh window inventory and individual launch recovered the helper. No native pass is inferred from that interrupted call. Later observations used the actual returned preview and primary EXE windows.

Final primary artifact fingerprints:

| File | SHA-256 |
| --- | --- |
| AriaGui.App.exe | `26EFD35520BC1B50CCA073491C41BB658DC11878A08ED97D3E2A512E68F42C84` |
| AriaGui.App.dll | `FA8C828A31FD25102747E825DE0F62B93F4522A234A344F8C4EDF2CE82FF3544` |
| AriaGui.App.pri | `879AE9D99C78FAE6F367B7AE2ACFA2F13E42D937F85EBA2EACD3AA9A5F00B7EE` |

The EXE is a stable apphost; the updated DLL and PRI fingerprints identify this redesign build. `artifacts/desktop-ui-preview` predates the final checkbox corrections; use `artifacts/exe/AriaGui.App.exe` for the final UI.

## Known limits

- The initial history discrepancy in `NATIVE_EXE_TEST.md` remains open until separately reproduced and diagnosed.
- Clean-machine MSIX installation and upgrade acceptance remain separate release gates.
- Unsupported prototype controls (categories, torrent details, connection limits and browser handoff) are outside this refresh.

