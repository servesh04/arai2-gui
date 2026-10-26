# AriaGUI agent rules
Project: WinUI 3 / C# / Windows App SDK GUI for aria2. The HTML prototype is a visual reference only.

1. Edit only files assigned to your phase; if you need another, stop and say so.
2. Never create .md files. Final reply: at most 10 lines.
3. Tests: UI-only work = build + launch only. Backend: tests only for new logic, run only the affected project/filter, once. Never run the full suite (only the integration phase does).
4. Screenshots: max 2, one gitignored folder, fixed names, delete afterwards. No hashes, artifact folders, profile copies, journals or instrumented builds.
5. Styling only through the theme ResourceDictionary tokens; no hard-coded colors or sizes in views.
6. Unimplemented backend features: hide the control, or disable it with a tooltip. Never leave a visible control that does nothing.
7. Keep keyboard focus, accessible names and High Contrast working; every icon-only control needs a tooltip and AutomationProperties.Name.
8. Never touch production preferences or published binaries.
9. DownloadsPage and the shell are composition roots, owned by the integration phase; feature phases request wiring instead of editing them.