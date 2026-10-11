# How the desktop refresh works

The refresh changes the native Windows workspace around the existing download engine. It keeps the coordinator, saved command intents, SQLite history and recovery rules introduced in earlier phases.

## 1. The view draws the workspace

`src/AriaGui.App/Views/DownloadsPage.xaml` describes the menu, toolbar, sidebar, transfer list and details pane. XAML creates native WinUI controls. Its code-behind connects Windows-specific interactions such as selection, column headers, splitters and dialogs to the ViewModels.

The transfer list is virtualized: it creates controls for the visible rows and reuses them while scrolling. A download's identity therefore comes from its stable job ID, never from a row control or its current position.

Add and Preferences have their own ContentDialogs. They share the existing ViewModels, so moving a form into a dialog does not create a second download engine or a second preferences store.

## 2. ViewModels connect gestures to behavior

`DownloadsViewModel` owns the visible rows, current search/filter, sorting and selection. `DownloadItemViewModel` exposes each row's labels and supported actions. Progress updates change these observable properties, allowing WinUI bindings to update the screen.

Selection tracks job IDs. Before a batch executes each item, it checks the latest state and connection again. A file may have completed or another command may already be pending since the user selected it.

`DownloadBatch` distinguishes confirmed, unconfirmed, skipped, failed and canceled outcomes. An RPC task returning is not sufficient evidence that the requested state was reached: the saved intent and observed transfer state determine the result. This preserves the earlier recovery model when the engine connection is interrupted.

## 3. Sorting and queue order answer different questions

A column sort changes how the user browses transfers. Moving a waiting download up or down changes the actual engine queue through the existing coordinator.

Sorts compare underlying values, such as byte counts, rather than formatted strings such as “900 KiB” and “2 MiB”. Saved history applies the requested sort to the matching records before choosing a page, so the first page is actually the first page of that ordering.

## 4. Preferences keep the workspace familiar

`UserPreferences` adds a validated `DesktopLayout` containing column order, widths and visibility, row density, pane dimensions and visibility, and the animation preference. Older preferences without a layout receive defaults.

The preferences ViewModel serializes saves so a layout update and a theme/folder save cannot overwrite each other with stale values. Layout saves preserve the already saved theme and folder. Pane changes use a short debounce to avoid writing on every mouse movement; explicit Exit flushes the final layout before services are disposed.

## 5. Verification uses several layers

Behavior tests exercise selection, eligibility, races and persistence without opening a window. Infrastructure tests exercise real stored history and preferences. A native smoke test then checks the things those tests cannot prove: compiled XAML resources, dialog usability, table layout, Windows input and a real transfer through the running EXE.

See `DESKTOP_UI_TEST.md` for the current results and remaining manual checks. A successful build does not by itself prove DPI, high-contrast or assistive-technology usability.
