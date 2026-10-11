# R0 runtime and settings provenance

9 October 2026. Read-only observations supplement `R0_NATIVE_MEASUREMENTS.md`; no app data was changed during these reads.

Approved process inspection identified `AriaGui.App.exe` PID 8208 at `C:/Stuff/Arai2GUI/artifacts/exe/AriaGui.App.exe`. Its child `aria2c.exe` PID 15136 reported its configuration path under `C:/Users/User/AppData/Local/AriaGUI/Engine/aria2.conf`. Only the configuration path was extracted; configuration contents and RPC credentials were not printed.

An approved read of `C:/Users/User/AppData/Local/AriaGUI/Engine/preferences.json` found the following saved values:

| Setting | On-disk value |
| --- | --- |
| Modified | 9 October 2026, 15:27:24 +05:30 |
| Theme / default folder | System / empty (default destination) |
| Columns, in order | Name 250.4000244140625; Size 140; Status 140; Progress 150; Speed 110; ETA 130 |
| Visibility | ETA hidden; remaining columns visible |
| Density | Comfortable |
| Sidebar / details | Both visible; preferred width 160 / height 180 |
| Animations | Enabled |
| File SHA-256 | `5D47898973731ED788EC9EF64194D22C672106BB9D0923962F30CEDCF81D8638` |

These values agree with the earlier Sol smoke-test settings but differ from the current native window: that window appears to show six columns, a wider Name column, a narrower sidebar and two records. The native report owns measured geometry and record observations. This approved read reproduced the discrepancy from the earlier file read; it did not resolve it.

The source chooses either the unpackaged local application-data directory or the package LocalState directory in `src/AriaGui.App/Services/AppDataPaths.cs`. Preferences load asynchronously through `PreferencesViewModel.LoadAsync`. A configuration path, executable path and on-disk settings file do not prove which preference values a running view accepted or when. Do not treat this file as a verified snapshot of live UI settings, infer lost history, reset either data store, or overwrite preferences to make the observations agree.

R1 reproduction should record live effective geometry independently and retain this provenance limitation until the loaded settings/source relationship is established. Runtime tracing of loaded preference identity/revision, if needed, belongs in the authorized implementation step; R0 makes no instrumentation changes.
