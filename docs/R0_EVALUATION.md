# R0 evidence evaluation

9 October 2026. Astra review of `R0_LAYOUT_AUDIT.md`, `R0_NATIVE_MEASUREMENTS.md`, `R0_RUNTIME_CONTEXT.md`, the responsive plan and the saved maximized screenshot. No application edits, native interactions, builds or tests were performed for this evaluation.

**Verdict: R0 is partially complete.** The evidence supports beginning the established table correction, subject to the explicit R1 entry conditions below. It does not satisfy the plan's current requirement to complete every R0 item before R1: the reported large column mismatch remains unexplained, live preferences and client/DPI geometry are unresolved, and scrollbar/recycling/customization and minimum-size cases are missing. The prior 147-test result does not close these gaps.

## What is established

| Finding | Evidence and confidence |
| --- | --- |
| Table fails to occupy its wider workspace | Direct source evidence establishes an explicit canvas width independent of viewport. The saved maximized image clearly shows substantial space on both sides of the table; the native sequence reports that the gap varies with maximize/restore. This is a strong explanation for the reproduced lateral-space defect. Exact arrange behavior, centering symmetry and DIP gap remain unmeasured. |
| Filter/search has an unstable-looking placement | The maximized image confirms the capped field floating well short of the right edge. The star toolbar cell and 280 cap explain the source-level constraint. Screenshot coordinates are approximate image units, not verified DIPs. This belongs primarily to R2. |
| Header and rows have an inadequate shared contract | Independent configuration, fallback widths and gutter/inset accounting are established source facts. They justify R1's shared geometry contract. They do not prove which mechanism caused the user's large mismatch. A possible constant gutter difference alone cannot explain a large cumulative column-width discrepancy. |
| Current run did not reproduce the large mismatch | The native report records visibly consistent header/row alignment after maximize/restore, filter clear and horizontal scrolling. The maximized image agrees at visual inspection level. This is not a measured one-physical-pixel pass. Replacement automation identities after filtering do not establish offscreen virtualized-container recycling or its layout lifecycle. |
| Runtime provenance is useful but incomplete | Process path and EXE/DLL/PRI fingerprints identify the inspected artifact files. The reported live layout conflicts with the on-disk settings snapshot. File fingerprints alone do not establish the loaded view's preference revision or correspondence between current source and running binaries. Default 1030-DIP arithmetic must not be assigned to the observed table without that linkage. |

The native report appropriately identifies unavailable cases and preserves the interrupted session's limits. Its repeatable two-row sequence is useful evidence, but the proposed customization and many-row sequence is a future procedure, not an executed pass.

## Concrete R1 entry and completion requirements

1. Before changing layout behavior, establish a controlled baseline: record executable version/build-source identity alongside hashes; loaded preferences path/revision and actual ordered visible widths; actual client dimensions, rasterization scale, viewport/canvas bounds and header/row edges. Existing image dimensions must remain labeled as image dimensions. If lightweight diagnostic instrumentation is needed, capture the unchanged layout with it first. Resolve or explicitly isolate the settings discrepancy without overwriting user data.
2. Obtain a bounded baseline for reversible column resize/reorder and isolated many-row data with a vertical scrollbar and actual offscreen recycling. Record constant origin differences separately from cumulative column-width differences. If the original large mismatch remains unreproducible, retain it as an unresolved reported defect; do not name a callback/recycling cause as proven or claim that specific regression fixed.
3. Implement the single resolved width/order/visibility contract already proposed, with shared rounded edges, owned/measured native insets, one horizontal offset and application to realized/reused rows. Preserve finite-height virtualization, selection IDs and collection identity. A new hard-coded header offset is insufficient.
4. Before declaring R1 complete, compare before/after geometry through maximize/restore, filter clear, customization, horizontal scrolling, scrollbar appearance and recycling. Demonstrate the plan's boundary tolerance using actual scale, plus fit/manual behavior. These missing cases need not prevent work on the independently established fixed-width defect, but they block an R1 alignment/virtualization acceptance claim.

## Required plan clarification

Update the plan status to **R0 partially complete**, link these three evidence reports and this verdict, and replace the obsolete statement that no runtime reproduction occurred. Distinguish reproduced gap/search behavior from the unreproduced large header mismatch.

The existing strict R0 → R1 gate conflicts with proceeding now. If proceeding, explicitly permit R1 to start on established defects while carrying the listed baseline gaps; do not silently mark R0 done. Preserve the unresolved mismatch as a tracked verification item.

Keep 560 × 420 and 800/1100 DIP thresholds labeled **provisional design targets**, not supported measurements. Current R0 can measure existing clipping/content requirements; support for the redesigned states must be validated after R2/R3 and confirmed in R4. State that distinction in the R0 completion wording. No evidence in this run validates a minimum, breakpoint, DPI transition, pane recovery or compact dialog reachability.
