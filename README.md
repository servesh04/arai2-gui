# AriaGUI

Windows 11 x64 download manager built with .NET 10, WinUI 3, and aria2.

## Install and run

For an existing published build, keep the entire distribution folder together and run `AriaGui.App.exe`. A local build is at `artifacts/exe/AriaGui.App.exe` when present; `artifacts/` is generated and is not included in Git.

To build and run from source, install the .NET 10 SDK specified in `global.json`, then use PowerShell:

```powershell
.\scripts\Run.ps1 -Unpackaged
```

The first run restores dependencies. The included `vendor/aria2/win-x64/aria2c.exe` supplies the download engine. **Close** hides the app to the tray; use **File → Exit** to stop the engine and quit.
