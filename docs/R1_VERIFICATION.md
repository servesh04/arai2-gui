# R1 verification — independent Sol pass

10 October 2026. R1 current functional verification completed at 125% scale; historical baseline limitations remain below. This report records only executed checks.

## Unchanged-layout instrumented baseline

Built Debug unpackaged app with only the opt-in geometry observer added (UI agent confirmed no behavioral changes). Serialized command: `dotnet build src/AriaGui.App/AriaGui.App.csproj -p:Platform=x64 -p:AriaGuiUnpackaged=true --no-restore -m:1 -nr:false`. Passed with zero warnings/errors in 48.27 seconds. Earlier parallel project-reference build attempts silently failed; serial build resolved that tooling problem.

Native launch/input used the Computer Use skill and `@oai/sky`. The original primary EXE was initially minimized with Downloading filter and two records. It was restored and cleanly exited through File > Exit. No download command or deliberate preference/history write was performed. Debug launch showed seven existing records and loaded the customized five-column layout previously observed on disk. This isolates the original live/on-disk discrepancy as a different process/build snapshot; it does not establish its root cause or prove the original process's loaded preference revision.

Evidence: `artifacts/r1-layout/baseline-geometry.jsonl` and `baseline-restored.jpg`. Geometry comes from WinUI measurements in DIPs; screenshot dimensions are separately observation-image units. Rasterization scale was 1.25.

| Case | Client DIPs | Workspace | Table canvas | Finding |
| --- | --- | --- | --- | --- |
| Restored | 961.6 × 728 | x166.4, width795.2 | x166.4, width870.4 | Content wider than viewport; horizontal scrolling available |
| Maximized | 1536 × 880 | x166.4, width1369.6 | x415.4, width870.4 | Fixed canvas leading gap249 DIPs; spare horizontal width499.2 DIPs |

Loaded ordered preferred widths: Name250.4000244140625, Size140, Status140, Progress150, Speed110, hidden ETA130. Actual header/row widths agreed: 250.3999939,140,140,150.3999939,110.4000015,0. Header padding starts38 DIPs from canvas; row data grid starts32 DIPs from canvas. This yields a constant6-DIP origin mismatch (7.5 physical pixels at scale1.25), separate from cumulative width differences. Header text itself has an additional8-DIP padding, so glyph positions must not be used as boundary measurements.

R0 saved maximized screenshot was independently inspected: substantial lateral blank space and capped search reproduced. Its large reported cumulative header/body mismatch remains unreproduced and unresolved.

## Outstanding acceptance evidence

Post-change evidence is recorded below. Seven rows allowed a reduced-viewport scrollbar test, but do not prove offscreen recycling. An isolated many-row fixture is still needed for that gate. Loaded preference path/revision and unchanged-layout customization baseline remain missing. DPI transitions, other scale settings, exact narrow/minimum clients, snap and R2/R3 adaptive behaviors have not passed.

## Post-change native result

Latest corrected Debug build (including wide fit resize seed fix) passed using the same serialized build command: zero warnings/errors,25.91 seconds. Earlier responsive build also passed37.01 seconds.

At restored961.6×728 client DIPs, canvas width795.2 equals viewport795.2 and starts at workspace x166.4. Header padding32 matches row-grid origin32; all actual column widths match. At maximized1536×880, canvas width1369.6 equals viewport1369.6, with zero leading gap. Effective Name expands to796.8, other actual widths140/140/150.4/110.4. Header extent is0.8DIP beyond canvas (one physical pixel rounding), while corresponding column origins and widths agree. Automatic fit preserved original preferred widths.

Details divider drag542→320 observation-image coordinates reduced table canvas height380.8→158.4DIPs, showing approximately three rows and a native vertical scrollbar. Vertical scrolling changed visible rows and retained matching shared widths; measured row/header origin difference was under0.01DIP during animation. Reverse divider drag restored canvas380.8 and original details height180. Seven row grids remained realized even with the small viewport. This proves scrollbar and scrolling alignment, **not offscreen container recycling or large-list realization proportionality**.

Columns dialog Manual selection used original preferred widths; canvas822.4DIPs stayed left anchored. Horizontal scroll moved canvas/header together x166.4→138.4; row data origin retained the same32DIP inset. The accessibility scrollbar value remained stale0, so the measured transform supplies scroll evidence. Speed hide/show and Status move later/earlier were exercised and exactly restored. Diagnostics show corresponding header/row widths matching in these states. Grip resize was not executed because restoring floating-point preferred widths exactly through UI could not be guaranteed. Checkbox click/deselect was attempted; the available accessibility output did not expose checked state clearly, so no strong selection-persistence claim is made.

Evidence: `artifacts/r1-layout/after-restored.jpg`, `after-maximized.jpg`, `after-scrollbar.jpg`, `after-geometry.jsonl`. The latter includes the baseline followed by post-change samples; distinguish them by the header padding38→32 and canvas widths. All preference column IDs/order/visibility/widths returned to the baseline. Fit mode and original180DIP details height were restored. Original Downloading filter restored; Minimize was requested last; Computer Use returned `foreground window did not report a process id`, so the final minimized outcome was not confirmed.

## Test execution blocker

Commands executed:

- `dotnet test tests/AriaGui.Application.Tests/AriaGui.Application.Tests.csproj --no-restore -m:1 -nr:false -p:NuGetAudit=false`
- `dotnet test tests/AriaGui.Core.Tests/AriaGui.Core.Tests.csproj --no-restore -m:1 -nr:false -p:NuGetAudit=false`
- Core retry with `VSTEST_CONNECTION_TIMEOUT=180` and `--no-build --no-restore -m:1 -nr:false -p:NuGetAudit=false`.

All compiled test inputs; all runs **aborted before any reported test result**. VSTest could not connect to testhost after90seconds (Application pid24860/Core pid28320), and the Core retry failed after180seconds (pid28312). Passed count is unknown/zero reported, not a failed assertion count. Infrastructure/full suite was not run after the repeated environmental handshake failures. The previous147-test result is not current verification.

## Acceptance verdict

Established fixed-canvas gap and constant origin discrepancy are corrected in measured restored/maximized fit states at125% rasterization scale. Manual anchor, shared horizontal movement, scrollbar alignment and reversible hide/reorder were exercised. **R1 remains partially verified, not fully accepted:** unchanged-layout customization/scrollbar baseline, loaded preference identity/revision, actual recycled containers/large-list proportionality, grip resize and meaningful selection persistence are outstanding. Current tests did not execute due to the testhost blocker. Original large mismatch remains unresolved; no specific recycling cause is proven. R2/R3/R4 scale/minimum/adaptive matrices remain untested.

## Follow-up closure pass (latest evidence)

The earlier testhost blocker is isolated to execution context: the same existing no-build focused resolver command, executed outside the sandbox, completed **8/8** cases in73ms. The diagnostic log records a successful local127.0.0.1 testhost connection in `artifacts/r1-layout/testhost-focused.log`. This supports a sandbox handshake restriction; it does not identify the exact sandbox rule. Current rebuilt Application tests (including R2 policy cases) passed **79/79** in9seconds, and current Core tests passed **16/16** in112ms. Existing Infrastructure Preferences/History subset passed **2/2** in2seconds. The full existing Infrastructure suite was cancelled after more than4minutes without reported individual results; it is not a full-suite pass.

An isolated fixture seeder under `tests/AriaGui.UiFixture` created500 synthetic completed records in `artifacts/r1-layout/synthetic-profile`. It refuses any nonempty destination. A copied Debug app reads an explicit `r1-fixture-profile.txt` marker and requires a synthetic marker inside that directory. Its engine executable path points to an absent private fixture executable, so no engine/download is launched. Release builds ignore the activation marker. No production download database was changed. The copied preference baseline has SHA256 `5D47898973731ED788EC9EF64194D22C672106BB9D0923962F30CEDCF81D8638`; the original user preference hash remained identical after the native fixture pass.

The initial combined build passed45.01seconds; the shell crash/focus repair build passed32.10seconds. Both had zero errors and oneNU1900 audit network warning. The latter app DLL SHA256 was `0D7B5734A39217D6B29FC3843E2B703D3DB58DB03EFC235E9F302474DD6402B8`. Initial Computer Use launch stalled without a target window and was interrupted; a bounded repaired launch succeeded. All native input in this follow-up used the copied fixture.

The fixture live list loads100 of the500 completed records. At961.6×728 clientDIPs (scale1.25), it initially tracked43 row grids. Checkbox click selected job `a18ef705-c9f3-4c4e-a918-fb0d806e54a9`; that same ID remained selected after scrolling out of view. Deep scrolling rebound **42 existing grid identities to different job IDs**, proving actual recycling. Tracked grid count reached44, below100 loaded records. This is evidence for a bounded realization cache in this viewport, not a comprehensive memory/performance matrix.

**A real R1 failure was uncovered by recycling:** after1600 then6400 wheel units, the canvas grew to15600DIPs against795.2DIP viewport, header padding became`0,0,15000,0`, and columns shrank to semantic minima. The first cached row still reported IsLoaded while parked atX−14801.6/Y−18156.4. `MeasureTableInsets` chose that cached row and interpreted its parked transform as table chrome. This failure is independently reproduced; it does not establish that the user's original screenshot had the same cause. Evidence is `artifacts/r1-layout/fixture-before-recycling-fix.jsonl` and `fixture-deep-scroll.png`. Source repair is now undergoing a fresh serialized build/native rerun. Grip and R2 adaptive checks remain pending this rerun.

## Sequential Sol closure, 10 October

The repaired inset measurement only accepts a currently visible row with a valid native container and bounded chrome measurements; reserved scrollbar space comes from the native viewport rather than parked row extent. Earlier repaired evidence (`fixture-after-recycling-fix.jsonl`) confirms stable 795.2-DIP canvas after deep recycling, 42 grid identities rebound, retained job selection, a 40-DIP grip resize, and maximized Fit canvas1369.6 matching viewport1369.6.

The same agent built, exercised, and reviewed the latest source sequentially. Serialized latest app build passed in28.74seconds with zero errors and one cached NU1900 audit warning. App DLL SHA256: `D869045A811795100B1AEBEE9AAF4437FC52DFE2809BFD8D984BF48BB68F863D`. Application79/79 (10seconds), Core16/16 (244ms), and Infrastructure preference/history-filtered11/11 (23seconds) passed outside the sandbox. The Infrastructure selection also matches existing history-related cases beyond the earlier2-test subset; it is still not the full Infrastructure suite.

A separate copied Debug fixture under `artifacts/r1-r2-sol-pass/fixture-app` used physical startup marker830×820. Native geometry establishes **649.6×616 clientDIPs at1.25**, inside the supported compact width range. Fit canvas equals649.6 viewport, leading origin0, shared gutter32. Checkbox selection of `a18ef705-c9f3-4c4e-a918-fb0d806e54a9` persisted through6400wheel units.29 existing grid identities rebound to different jobIDs; maximum tracked grids30 versus100 loaded records. No parked-row chrome feedback recurred. A measured Name grip drag40DIPs increased its actual width186.4→226.4 and switched to left-anchored Manual canvas689.6. Fit was then restored through Columns. Synthetic fixture preferred widths changed deliberately during this grip test; production preferences were untouched.

Evidence: `artifacts/r1-r2-sol-pass/r1-compact-geometry.jsonl`, `r1-compact-fit.jpg`, and the earlier repaired fixture log. The production preference SHA256 remains `5D47898973731ED788EC9EF64194D22C672106BB9D0923962F30CEDCF81D8638`. The synthetic-marker gate and absent private engine executable were independently reviewed; no engine/download command was used. No release/main EXE was published.

**R1 current functional gates pass in these measured configurations**, permitting sequential R2 work. The historical unchanged-layout many-row/customization baseline was never captured and cannot be reconstructed as contemporaneous evidence; the original reported large mismatch and its causation remain unresolved. Do not erase those R0 limitations or infer a multi-DPI/performance pass. Supported minimum560×420, exact native boundary crossings, other DPI/text scales and the broader R3/R4 matrix remain outstanding.

### History-recreation regression found during R2

R2's compact→saved-history→search→maximize sequence exposed another real R1 lifecycle failure: after collection recreation, only the first row matched the expanded header while other visible rows retained compact widths. Cached row templates had been removed from registration on Unloaded, then reattached without a matching Loaded callback. Before evidence: `artifacts/r1-r2-sol-pass/r2-wide-history.jpg` and `before-history-row-fix.jsonl`.

The repair marks the inner data Grid and discovers native realized descendants during container changes and width resolution. Registration attaches a named Unloaded handler, which removes itself; rediscovery therefore does not accumulate anonymous handlers. New grids receive the current shared geometry, while existing grids are reconfigured only when geometry changes. This was reviewed against the actual saved before/after diff, not against an assumed Git baseline.

The same exact history/search/maximize sequence then aligned all13 visible rows, with maximum header/row width difference0DIPs, canvas1369.6 matching viewport. Evidence: `r2-wide-history-fixed.jpg` and `r2-repaired-compact-wide-geometry.jsonl`. A subsequent961.6×728DIP live-list rerun selected the same synthetic job, scrolled6400wheel units, tracked44 grids, retained selection, and kept canvas795.2 with visible width difference0DIPs. Evidence: `r1-final-regular-deep-scroll.jpg` and `r2-regular-wide-geometry.jsonl`.

Supported minimum560×420 was subsequently measured at1.25scale: semantic column minima produce632DIP canvas inside560DIP viewport, as intended, with116DIP table height. The short-client pane allocation still fails the planned180DIP transfer-height target and belongs to R3; this is not a full minimum-layout acceptance. R1's current table functionality passes in the recorded cases, with historical and broader-matrix limits preserved.

