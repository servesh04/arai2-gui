# R2 adaptive shell verification

10 October 2026. GPT-6.1 Sol sequential implementation and self-review. **Targeted R2 native verification passes at scale1.25, including final overlay focus, boundary crossings, hidden-sidebar intent and long notices. Broader text/DPI and pane recovery remain R3/R4 requirements before release.** Earlier observations below are historical; the final continuation supersedes their pending focus/boundary/notice status.

Scope: `DownloadsPage.xaml(.cs)`, `MainWindow.xaml(.cs)`, `ResponsiveShellLayoutPolicy` and its tests, against `RESPONSIVE_UI_PLAN.md`. Combined builds and native fixture operation are serialized by the R1 verifier; this reviewer did not start a competing build or UI session.

## Static findings

| Finding | Severity | Evidence and disposition |
| --- | --- | --- |
| Compact details resizing saves temporary sidebar width zero | P1, fixed in source | Compact layout sets `SidebarColumn.Width` to zero while `Preferences.SidebarVisible` remains true. Details drag/arrow handlers call `SavePaneLayout`, which originally copied that zero into the layout. `DesktopLayout.Validate` rejects widths below 120 and throws `DownloadRequestException` from the synchronous UI handler. Independently inspected fix: compact saves now preserve `Preferences.SidebarWidth`. Native splitter check remains pending. |
| Focus while an open filter overlay returns to docked mode | P2, mitigated in source | `RestoreDockedFilters` originally hid the flyout and reparented its content without restoring the focused descendant; the Filters button also collapses in docked mode. Independently inspected fix captures a focused SidebarHost descendant before hiding, then dispatches keyboard focus restoration after reparenting, with StatusFilters fallback. No observed runtime failure claimed; native continuity check remains pending. |

The source uses one `SearchBox` across states and changes Grid placement rather than recreating it. Its two-way search binding stays attached. The filter overlay reparents the same `SidebarHost` and status/history controls, so bound selection is retained structurally. Retry overflow binds to the same ViewModel command as the toolbar. Status filter counts occupy a separate Auto column; labels trim with tooltips. Empty/whitespace batch notices collapse, and long notices have a bounded scrolling region. Footer connection/count text has a full-status flyout in compact mode and tooltips in other modes.

Policy tests express inclusive 800/1100 DIP boundaries, measured-content thresholds, invalid-width handling and title-subtitle fit. These tests do not prove native command fit, overlay lifecycle, focus retention, clipping, or display/text scaling.

## Build and native status

The coordinating agent reported the **post-fix** combined build passed with zero errors and one NU1900 restore warning. Application tests passed 79/79; focused Infrastructure Preferences/History tests passed 2/2. These are verifier/coordinator-reported results, not a second execution by this reviewer. They replace the earlier pre-fix build result as compilation evidence.

The first copied-fixture native launch stalled for approximately 565 seconds with no confirmed R2 interaction. The bounded retry subsequently launched successfully, and the R1 verifier is conducting native checks. Screenshot/geometry review remains pending. This reviewer has made no competing UI calls.

Independently inspected `artifacts/r1-layout/fixture-deep-scroll.png`, captured in the regular layout at approximately 963 DIPs. The first toolbar row shows Add/Resume/Pause/More, with full-width search below. Docked status labels/counts, menu, engine health, aggregate speed and count fit in this image; connection text trims. This is qualitative regular-layout evidence only. It predates a subsequent R1 table-measurement correction and establishes neither compact/wide behavior nor focus continuity.

Independently inspected `fixture-wide.png`: commands including Retry fit on the left, search is anchored on the right of the same row, and menu/sidebar/footer fit. `fixture-compact.png` is **not Compact evidence** despite its filename: it shows the full Add caption, a docked sidebar and search on the second row, consistent with Regular. Client-DIP provenance and a genuinely compact fixture are still required. The wide image is qualitative evidence, not threshold/focus verification.

Independently parsed nonzero-client records in `fixture-after-recycling-fix.jsonl`: recorded widths include 961.6, 1536 and 292.8 DIPs at scale 1.25. The 1536 × 880 DIP snapshot has canvas width 1369.6 and one selected job. This establishes measured wide provenance; R1 table conclusions belong to its own report.

`fixture-compact-294.png` independently shows compact shell behavior: short Add, Filters, a second command row, search on its own row, no docked sidebar, compact Status footer. Its measured width is only 292.8 DIPs, **below the supported 560-DIP minimum**, and the menu does not fully fit. It cannot establish supported compact acceptance. The verifier reported that a details drag there retained preferred sidebar width 160 and changed details height to 210.4, and that Filters opened and Ctrl+F closed the overlay/focused search. These interaction results are verifier-reported rather than independently repeated. A supported approximately 650-DIP fixture remains pending.

## Earlier outstanding evidence (superseded where closed below)

- Executable identity for the successful post-fix combined build/native fixture.
- Native wide/regular/compact layouts, including supported minimum and threshold crossings in both directions.
- Search value and focus through resizing; Ctrl+F while filter overlay is open.
- Status/history selection through overlay dismissal and docking; keyboard focus restoration.
- Compact details splitter interaction and recovery of preferred docked sidebar width.
- Toolbar/More/menu access, history paging, long notices, footer/title visibility and page clipping.

R3 panes/dialogs/persistence and R4 multi-DPI/text-scale matrix remain separate acceptance work. A static source review or existing R1 screenshot is not a native R2 pass.

## Sequential native and source closure, 10 October

One agent completed R1's current functional checks and report before starting R2. During R2, history recreation uncovered an additional row-registration regression; it was repaired and the exact failing sequence was rerun successfully before proceeding. See R1 for its measured table evidence.

All native interaction used Computer Use `@oai/sky` in copied Debug fixtures with a marked synthetic profile and absent private engine executable. No download/engine command or external network/download action was used. Production preferences retained the hash recorded below. No main/release EXE was published.

Authoritative production preference SHA256: `5D47898973731ED788EC9EF64194D22C672106BB9D0923962F30CEDCF81D8638`; this was re-read after the final build. Fixture preferences were deliberately customized and reset only within the synthetic directory. They are not production settings evidence.

| Measured native client | Executed result at1.25 scale |
| --- | --- |
| Wide1536×880DIPs | Commands including Retry fit on the left,420DIP search anchors right, sidebar docks at preferred160, full title subtitle and footer fit. History controls remain reachable. |
| Regular961.6×728DIPs | Sidebar remains docked; Add/Resume/Pause/More fit on the first row, search stretches below; Retry is in More. Menu, footer and title fit. |
| Compact649.6×616DIPs | Short Add, labeled Filters, Resume/Pause/More and full-width search fit. Sidebar overlay retains Completed and Saved history. History buttons move below the description. |
| Supported minimum560×420DIPs | Menu/Add/Filters/More/search/engine health/speed fit. Table intentionally scrolls horizontally. Transfer canvas height116 is below the planned180 target: R3 pane allocation remains required. |

Search `synthetic-04` and its keyboard focus survived compact→wide→compact, with100 matching history records; compact Next advanced Page1→2. Separate regular→wide→regular retained `synthetic` and search focus. Ctrl+F dismissed the filter overlay and focused the same search field. Completed selection and Saved history survived overlay dismissal and docking; a wide screenshot shows the checked history control and current page. More exposed Retry/Recover/queue/file actions with the existing eligibility rules. Compact Status displayed the full connection explanation and count. File→Exit was reachable at compact, regular and minimum sizes. Native screenshots/geometry are under `artifacts/r1-r2-sol-pass`.

Compact details drag persisted preferred sidebar160 and details200.8, avoiding the previously reviewed zero-width exception. This proves that particular crash repair; it does not close R3's durable pane-height recovery rules.

**Minimum overlay failure and executed repair:** Filters originally extended below the short client. Its existing ScrollViewer is now bounded by client height on opening and resizing; docking removes the temporary MaxHeight. The final minimum overlay stayed above the footer and scrolling reached Saved history and Preferences. Saved history was activated through its settled accessibility target. Evidence: `r2-minimum-overlay-capped.jpg`, `r2-minimum-history-reachable.jpg`, and `final-app/table-geometry.jsonl`. One coordinate click during scrolling opened Preferences incidentally; it was dismissed without changing settings, then the settled accessibility target was used. No dialog-layout pass is claimed.

**Focus failure reproduced; final repair not yet verified natively:** Clicking a caption to maximize dismisses the flyout first and cannot establish focus continuity through pure resizing. A small Debug-only synthetic-fixture accelerator (Ctrl+Shift+R) rereads the existing physical-size marker once; it is excluded from Release and normal Debug and uses no watcher/polling. It successfully resized649.6→961.6DIPs while the overlay's Saved history control had visible keyboard focus. After docking, Space opened More instead of toggling Saved history, independently reproducing lost focus. Evidence: `r2-focus-before-resize.jpg` and `focus-app/table-geometry.jsonl`.

The final source repair records the routed GettingFocus target on SidebarHost and restores it after Flyout.Closed, after the flyout's own opener-focus restoration. It falls back to the status list if the target cannot focus. Compilation succeeded; **native verification remains pending**. The helper subsequently reported user input/minimization; one prescribed recovery did not provide an available window. Native input stopped rather than overriding repeated user activity. The copied focus fixture was left in that user-controlled state.

Final serialized build passed26.30seconds, zero errors and one cached NU1900 warning. Final app DLL SHA256: `F8006442D20CE91C1ED240BF509540473D54C21358E68CAB0FD4F739A5CB7FF9`. Earlier exercised row-repair build passed48.53seconds; overlay-cap build27.51seconds; fixture accelerator build47.75seconds. Existing Application79/79, Core16/16, and Infrastructure preference/history-filtered11/11 passed outside the sandbox; final Application rerun passed79/79 in8seconds after the focus repair. The full Infrastructure suite was not rerun or claimed passed.

The same agent reviewed all R1/R2 source and its own real snapshot diff (`source-before-after.diff`). Marker gating, realized-row registration/cleanup, shared column order/widths, command reuse, saved sidebar intent, overlay reparenting, bounded notices and title/footer policies were inspected. No source-only review is substituted for the pending focus runtime check.

Remaining R2 acceptance: rerun focus-preserving overlay→dock after the final fix; native one-below/at/above800/1100 boundaries in both directions; long batch/error/completion notices; larger text/localization and intentionally hidden-sidebar behavior. Broad multi-DPI, monitor moves, pane/dialog recovery and persistence remain R3/R4. The policy unit tests cover inclusive logical boundaries, but exact native threshold crossings were not executed. **R2 implementation is ready for continued verification, not fully accepted or released.**

## Final sequential native continuation — 10 October

The same GPT-6.1 Sol agent resumed verification, implementation and self-review without additional agents. One documented activation recovered the previously minimized fixture. It was cleanly exited through File→Exit. A fresh copied Debug fixture (`focus-final-app`) used compiled DLL `F8006442D20CE91C1ED240BF509540473D54C21358E68CAB0FD4F739A5CB7FF9` and the existing synthetic profile; the production EXE was never launched or replaced.

**Final focus repair passed:** select Completed in the compact Filters flyout, Tab to Saved history (visible keyboard ring), then use the fixture-only Ctrl+Shift+R resize with physical marker1220×960. Client649.6×616 became961.6×728DIPs. Saved history retained its keyboard ring after docking; Space checked it and loaded500 matching historical downloads instead of opening More. UI Automation continued reporting the old SearchBox focus while the popup owned focus, so the visible ring and actual Space behavior are the decisive evidence. Screenshots: `r2-final-focus-before.jpg`, `r2-final-focus-docked.jpg`, `r2-final-focus-space-history.jpg`.

**Native boundaries passed:** client799.2→800→1099.2→1100→1099.2→799.2DIPs, all at728DIP height and scale1.25. Search `synthetic-04` retained its value and keyboard focus at every step; Completed and Saved history remained selected with100 matching records. Compact uses Filters and a second history-navigation row; regular docks filters and retains a separate search row; wide moves search onto the command row and exposes Retry. Menu, caption controls and footer stayed accessible. The physical pixel grid at125% allows0.8DIP increments, so these are nearest representable below-boundary observations, not literal799/1099DIPs. Additional800.8/1100.8 observations passed with intentionally hidden-sidebar preferences. See `r2-boundary-*.jpg`, `r2-hidden-800-above.jpg`, `r2-hidden-1100-above.jpg`, `r2-native-boundaries.jsonl` and `r2-notice-geometry.jsonl`. Unit tests cover logical inclusive thresholds.

**Hidden sidebar and long notices passed:** a separate copied `notice-profile` retained SidebarWidth160 with SidebarVisible=false. At1100×728,649.6×616,800.8×728 and1100.8×728 the sidebar remained hidden, Filters stayed available, and its overlay could select Completed. No resize saved a temporary zero sidebar width. The synthetic notices were seeded once before binding from `r2-fixture-notices.json`, using a small `#if DEBUG` method and a caller gated by the validated synthetic-profile marker. This path runs no engine command, watcher or persistent-setting mutation and is absent from Release; normal Debug never reads the notice file. All three actual VM properties/bindings were exercised. Wide and compact screenshots show wrapping batch/error/completion content within the120DIP scrolling region; scrolling reached the final completion line. Table, menu and footer remained visible. Evidence: `r2-notice-wide-hidden.jpg`, `r2-notice-wide-completion.jpg`, `r2-notice-wide-end.jpg`, `r2-notice-compact-batch.jpg`, `r2-notice-compact-hidden.jpg`, `r2-notice-compact-end.jpg`, `r2-hidden-wide-filters.jpg`. Compact with all notices and details visible leaves a149.6DIP transfer canvas; this does not satisfy the planned180DIP target and remains an R3 pane-allocation requirement.

Final notice-hook build passed48.51seconds,0errors and one cached NU1900 audit warning (`--no-restore`, no download). Copied DLL SHA256: `38706771B975473D39E8F4AB9BF808BA8CD3390BFB445AEA329DFC2A5B8CFF22`. Application tests were rebuilt and rerun serially:79/79 passed in9seconds. Earlier Core16/16 and filtered Infrastructure11/11 results remain valid; the full Infrastructure suite was not rerun. No further production behavior changed after the F800 focus/boundary pass, only the gated synthetic notice seed. The same agent reread its complete actual source diff, including marker gates, focus timing, row rediscovery and named cleanup; snapshot diff remains `artifacts/r1-r2-sol-pass/source-before-after.diff`. This continuation adds `DownloadsViewModel.cs` to the earlier three changed source files.

Both fresh fixtures were cleanly exited. The original synthetic profile was retained, and the separate hidden-sidebar fixture retains its own explicit preference revision. Production preferences SHA256 stayed `5D47898973731ED788EC9EF64194D22C672106BB9D0923962F30CEDCF81D8638`; production data was not changed. R2's targeted shell verification is complete at the observed125% scale. Larger text/localization, other DPI/monitor transitions and performance remain untested; durable pane/dialog recovery remains R3, broader acceptance R4. Historical R0/R1 baseline gaps and the original reported mismatch's unresolved causation remain unchanged. No main EXE was published.
