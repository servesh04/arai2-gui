# R0 native layout measurements

9 October 2026. **Evidence only; no application code, user downloads or saved preferences were changed.** The Computer Use session ended when the delegated GPT-6.1 Sol agent hit its usage limit after restoring the initial filter/window state. Five screenshots had already been saved under `artifacts/r0-layout`; this report preserves the evidence delivered before that interruption.

## Tested build and initial state

The returned native window belonged to the running unpackaged executable at `C:/Stuff/Arai2GUI/artifacts/exe/AriaGui.App.exe`. Current artifact fingerprints are:

| File | SHA-256 |
| --- | --- |
| `AriaGui.App.exe` | `26EFD35520BC1B50CCA073491C41BB658DC11878A08ED97D3E2A512E68F42C84` |
| `AriaGui.App.dll` | `FA8C828A31FD25102747E825DE0F62B93F4522A234A344F8C4EDF2CE82FF3544` |
| `AriaGui.App.pri` | `879AE9D99C78FAE6F367B7AE2ACFA2F13E42D937F85EBA2EACD3AA9A5F00B7EE` |

The window was initially minimized. After restoration it showed the Downloading filter with no matching rows and a total of two records. Selecting All transfers revealed `slow.bin` as Paused and `small.bin` as Completed. No download, command or queue mutation was performed. The initial Downloading filter and minimized window state were restored before the agent stopped.

The native API supplied logical screenshot dimensions but no direct client-area, DPI or rasterization-scale property. Consequently, dimensions below are observation-image dimensions rather than a proven WinUI client-DIP measurement. Display scale and physical-pixel conversion remain unmeasured.

## Observations

| Case | Evidence | Result |
| --- | --- | --- |
| Restored window | `artifacts/r0-layout/01-restored-two-rows.png`, 1085 × 762 image | Two-row layout observed; table/header usable. This is not an exact requested 963 × 765 or 800 × 600 client-size case. |
| Maximized window | `artifacts/r0-layout/02-maximized-two-rows.png`, 1536 × 912 image | Reproduced a large lateral gap: the fixed-width table is visually centered inside a much wider workspace. Approximate screenshot measurement is 129 px from workspace start to table and 119 px from table to workspace end. The differences are estimates from the screenshot, not control bounds. |
| Toolbar search | same maximized image | Search remained about 280 px wide around x=820 rather than occupying or anchoring clearly to the right edge. This reproduces the reported floating/capped-filter behavior. |
| Filter clear / realization | `artifacts/r0-layout/03-maximized-filter-clear.png` | Filtering to `small.bin` and clearing created a new realized container according to the UI automation identities. Header and visible rows remained consistently aligned after this transition. |
| Maximize / restore | images 01–03 and direct observation | Table gap changed with available width; no large header/body mismatch appeared in this build. The same two records remained. |
| Horizontal scrolling | `artifacts/r0-layout/04-horizontal-scroll.png` | A horizontal wheel operation produced a reported scroll value of approximately 194.4000244. Header and body moved together in the screenshot. No stale-width mismatch appeared. |
| Cleanup/state restoration | `artifacts/r0-layout/05-restored-initial-filter.png` | Restored window and initial Downloading filter. No saved customization or record was deliberately changed. |

The reported user screenshot remains valid defect evidence. This R0 pass directly reproduced its gap and filter-placement problems. It did **not** reproduce the screenshot's apparent Size-under-Name/header mismatch after filter recreation, maximize/restore or horizontal scrolling. Current source still lacks a shared measured header/body layout contract, so the static risk remains actionable for R1; the large mismatch cannot be assigned to recycling from this run.

## Runtime/settings discrepancy

Approved read-only process inspection identified the app process at the primary artifact path and its child aria2 process using the unpackaged runtime configuration directory. An approved read of `C:/Users/User/AppData/Local/AriaGUI/Engine/preferences.json` produced the values recorded in `R0_RUNTIME_CONTEXT.md`.

Those saved values showed five visible customized columns, comfortable density and a 160-wide sidebar, while this running UI appeared to show six columns, a wider Name column, a roughly 130-wide sidebar and only two records. The settings matched the prior native smoke customization but did not match this live observation. Executable and engine configuration paths alone do not prove which settings snapshot the current view loaded or when it loaded it. No preference rewrite, database reset or restart was performed to force agreement. R1 should instrument or otherwise establish the loaded preference identity/revision before treating on-disk JSON as live geometry.

## Unavailable R0 cases

Two border-drag attempts and the system Size menu did not change the window dimensions through the available native control API. A snap attempt was rejected because the drag endpoint lay outside the window bounds. The agent hit its usage limit before a bounded follow-up could complete. Therefore the following remain untested, not failed:

- exact 800 × 600 and candidate 560 × 420 client sizes;
- wide/short and narrow/tall variants;
- supported minimum and the provisional 800/1100-DIP breakpoints;
- vertical-scrollbar/many-row recycling, because only two records were available and R0 did not add test records;
- sidebar/details preferred-size recovery after an actual narrow-window clamp;
- DPI, text scale, high contrast and multiple-monitor transitions;
- dialog reachability at compact sizes.

These gaps do not block R1's established fixed-canvas correction. They do prevent declaring the proposed breakpoints or minimum supported size validated. R2/R3 must implement measurable adaptive states before R4 can complete those cases.

## Repeatable sequence for R1 verification

1. Start the exact published build and record EXE/DLL/PRI hashes, active preferences identity and loaded layout revision.
2. Show two rows, restore the window, maximize it, then restore it. Capture viewport and table leading/trailing bounds rather than estimating glyph positions.
3. At maximized size, filter to one row and clear. Record header boundaries and the same realized row boundaries before and after container replacement.
4. Exercise one reversible column resize and reorder, then restore their exact preferred values. Repeat boundary measurements.
5. Repeat with enough isolated fixture rows for a vertical scrollbar and row recycling. Verify a common horizontal offset and ≤1 physical-pixel boundary difference after DPI conversion.
6. Once R2/R3 exist, use controlled client-size/DPI instrumentation for the provisional compact/regular/wide cases; do not infer those passes from screenshots.

The direct R0 conclusion is narrow: fixed table sizing and capped search placement are reproduced; the large column mismatch, compact-size behavior, pane recovery and DPI behavior require further controlled evidence.
