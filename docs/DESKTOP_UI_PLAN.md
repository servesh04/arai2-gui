# Desktop UI refresh

Received 8 October 2026 from the design chat, “Redesign aria2 GUI interface”. The supported native workspace, desktop interactions and layout customization were coded on 9 October. See `DESKTOP_UI_TEST.md` for verification evidence and remaining manual checks, and `DESKTOP_UI_WALKTHROUGH.md` for the code explanation.

**Resize review — 9 October 2026:** the user reported table gaps, header/body misalignment and inflexible filtering in a maximized window. The next work is defined in [Responsive UI plan](RESPONSIVE_UI_PLAN.md): shared header/row geometry, fit/manual table sizing, adaptive toolbar/sidebar/details/dialogs, preferred-versus-effective dimensions and a native resize matrix. This supersedes the fixed-width layout approach. It is a documentation-only planning update; responsive changes are not implemented yet.

References reviewed:

- `C:/Users/User/Documents/Codex/2026-10-08/thi/outputs/aria-desktop-design.html`
- `C:/Users/User/Documents/Codex/2026-10-08/thi/outputs/aria-desktop-handoff.md`

The HTML demonstrates appearance and interactions with simulated data. Translate it into the existing C#/WinUI 3 application. The earlier spacious web-style concept is superseded.

## Fit with the existing code

The application coordinator, durable command intents, SQLite history, owned aria2 engine and tray lifetime remain the foundation. Replace the card-oriented DownloadsPage with a compact desktop workspace. Reuse stable job IDs and DownloadItemViewModel commands; the inspector displays the same real transfer state as the table.

| Design feature | Current support / work required |
| --- | --- |
| Name, size, progress, status, speed, ETA | Existing transfer data; expose raw values for correct numeric sorting rather than sorting formatted labels |
| Pause, resume, retry, recovery, queue movement, open destination | Existing commands; share eligibility rules between toolbar, context menu and keyboard actions |
| Ctrl/Shift selection and batch commands | New UI selection model keyed by job ID; recheck eligibility at execution and report individual successes/failures |
| Sidebar status filters and counts | Reuse existing status rules; distinguish loaded/recent counts from whole-database counts |
| Saved history sorting/filtering | Preserve 50-row pages; add database query sorting where needed, avoiding a misleading sort of only the current page |
| Column widths/order/visibility and pane layout | New validated, bounded preference fields; preserve existing theme/default-folder settings and older JSON compatibility |
| General/file details | Existing destination, status, error and progress; display unknown values honestly |
| Categories | New app-owned job metadata and persistence; not engine queue priority |
| Connections, sources and speed limits | Current client does not expose these controls/details; verify engine capabilities and implement contracts, persistence and reconciliation before exposing them |
| Torrent/Metalink, peers/trackers, file priority | Current app deliberately accepts HTTP/HTTPS and disables torrent/metalink following; separate protocol work before showing these controls |
| Remove | Existing action stops a transfer and retains history/files. Do not relabel it as deleting a row or promise the prototype's Undo without implementing those semantics |

## Implementation increments

1. **Desktop workspace.** Compact title/menu/command bars, status sidebar, virtualized transfer table, adjustable sidebar/details splitters, real status bar and overview inspector. Put Add and Preferences in native dialogs so they do not consume the transfer viewport. Start with the six supported columns.
2. **Desktop interactions.** Native multiple selection, keyboard navigation, select-all-visible, right-click and overflow actions, batch command feedback, sorting/filtering and waiting-queue movement. Preserve selection through progress updates. Keep visual sorting separate from engine queue order; do not animate or continually reorder rows during selection or command execution.
3. **Customization and supported details.** Persist column layout, compact/comfortable density, pane sizes and visibility. Introduce categories and advanced engine controls only with their underlying implementation and appropriate migrations/tests. Fetch inspector details only for the selected job rather than expanding every polling response.
4. **Native validation and polish.** Respect Windows animation/high-contrast settings and the app animation preference. Prefer approximately 31/39 logical-pixel rows with room for accessibility scaling. Test small windows, high DPI, keyboard/screen-reader use, selection after filtering, mixed-state batches, layout persistence and real transfer recovery.

Each increment must build and run before the next. Existing download/recovery checks remain applicable. New tests should cover behavioral risks such as batch eligibility, stale selection, layout validation and paged sorting rather than simply mirroring XAML. The responsive follow-up proceeds through R0–R4 in `RESPONSIVE_UI_PLAN.md`; verification at one window size is insufficient to complete native layout acceptance.

## Related work and release status

The dense workspace directly addresses the restricted list viewport observed in the EXE smoke test. The initial history discrepancy in NATIVE_EXE_TEST.md remains open; a visual redesign does not resolve it. Clean-machine MSIX acceptance also remains open.

Browser handoff is a separate feature track: an extension/native bridge can submit supported downloads through the existing coordinator. The desktop refresh should provide a natural Add entry point for it, without assuming browser credentials, partial-file import or fresh-link recovery already exist.
