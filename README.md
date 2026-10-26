<p align="center">
  <img src="docs/design/assets/ariagui-app-icon.svg" width="88" alt="AriaGUI download arrow logo">
</p>

<h1 align="center">AriaGUI</h1>

<p align="center">
  A native Windows download manager powered by aria2.<br>
  Manage transfers, organize your queue, and keep downloads running from the tray.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows_11_x64-0078D4" alt="Platform: Windows 11 x64">
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
  <img src="https://img.shields.io/badge/UI-WinUI_3-00A4EF" alt="WinUI 3">
  <img src="https://img.shields.io/badge/engine-aria2_1.37.0-16A085" alt="aria2 1.37.0">
  <img src="https://img.shields.io/badge/status-stable-2E8B57" alt="Status: stable">
</p>

<p align="center">
  <a href="#-quick-start">Quick start</a> ·
  <a href="#-features">Features</a> ·
  <a href="docs/HOW_IT_WORKS.md">How it works</a> ·
  <a href="docs/DEVELOPMENT.md">Developer guide</a> ·
  <a href="https://github.com/servesh04/arai2-gui/releases">Releases</a>
</p>

> **Before you start:** AriaGUI targets **Windows 11 x64**. The app project is versioned **1.0.0**. Closing the window hides it in the tray. Use **Main menu → Help → Exit** to quit and stop the managed engine.

## 🚀 Quick start

### Run from source

You need Windows 11 x64, PowerShell, Git, and the **.NET SDK specified in [global.json](global.json)** (currently `10.0.301`, with later patches allowed). The initial restore needs internet access.

```powershell
git clone https://github.com/servesh04/arai2-gui.git
cd arai2-gui
.\scripts\Run.ps1 -Unpackaged
```

The script restores dependencies, builds the app, and launches it. Unpackaged mode does **not** require Windows Developer Mode. The pinned aria2 executable is included in the repository, so you do not need to install or configure aria2 separately.

For IDE development, use **Visual Studio 2026** with the **WinUI application development** workload. Open `AriaGui.slnx`, select `AriaGui.App` as the startup project, and choose **Debug / x64**. See the [developer guide](docs/DEVELOPMENT.md) for debugging and packaged launch instructions.

### Run an existing portable build

If you have a portable ZIP from the [releases page](https://github.com/servesh04/arai2-gui/releases):

1. Check its SHA-256 against the accompanying `SHA256SUMS.txt`, when provided.
2. Extract the **entire** archive and keep its files and subfolders together.
3. Run `AriaGui.App.exe`.

The stable portable package includes the .NET runtime, Windows App SDK runtime, and aria2. Source builds are framework-dependent for .NET. Generated builds such as `artifacts/exe/AriaGui.App.exe` are local outputs and are not included in Git.

Read the [release notes](docs/RELEASE_NOTES.md) for that package's features and limits. The portable ZIP is unsigned; Windows 11 clean-machine acceptance and signed installers remain pending.

## ✨ Features

These describe the source checkout. Availability in a downloaded package depends on its version.

| | What you can do |
| --- | --- |
| 📥 **Download management** | Add HTTP/HTTPS downloads, pause and resume transfers, retry failures, and manage the queue. |
| 🗂️ **Browse your transfers** | Search, filter by status, inspect transfer details, and revisit saved history. |
| 🌱 **Torrent files** | Import local BitTorrent v1 `.torrent` files, select files, review space requirements, and choose a destination. v2 and hybrid torrents are rejected. |
| 🎛️ **Bandwidth control** | Use speed presets, aggregate and per-download limits, weekly schedules, and temporary overrides. |
| 🛡️ **Verified downloads** | The guarded HTTP backend supports expected SHA-256 verification and eligible transfers using 2 or 4 connections. UI exposure depends on the workflow. |
| 💾 **Recovery** | Persist download intent, history, and engine checkpoints to reconcile unfinished transfers after interruptions. |
| 🖥️ **Desktop integration** | Keep the app in the tray, use light/dark/High Contrast theme resources, and configure notification preferences. |
| 🌐 **Browser handoff** | Develop and test the Chrome/Edge extension and native host. Capture requires explicit setup and opt-in; it is not a browser-store release. |

AriaGUI is a **C# / WinUI 3 desktop app**. The HTML prototype is a visual reference, not the application runtime. The GUI exposes a subset of aria2's capabilities; aria2 protocol support alone does not mean a feature is available here.

## 🧭 First download

1. Launch AriaGUI and wait for the engine to become ready.
2. Open **Add**, paste a public HTTP/HTTPS URL, and choose an existing destination folder.
3. Start the download and follow its progress in the transfer list.
4. Use the transfer actions to pause, resume, retry, or inspect details.
5. Close the window to keep AriaGUI in the tray; use **Main menu → Help → Exit** when finished.

The default destination is `Downloads\AriaGUI` under your user folder. You can change the default in Settings. For torrent intake, use the Add dialog's local torrent-file workflow and review the sharing notice before continuing.

For browser testing, follow [the unpacked Chrome extension instructions](extension/INSTALL-unpacked.txt). Keep the app and extension folders in stable locations, register the host through **Settings → Integration**, and enable capture in both the app and extension. Older cross-browser development tooling has [its own README](extensions/aria-gui/README.md); it is a separate path.

## 🛠️ Build and contribute

Run these commands from the repository root to restore the pinned dependency graph and build:

```powershell
dotnet restore AriaGui.slnx -p:Platform=x64 --locked-mode
dotnet build AriaGui.slnx --no-restore -p:Platform=x64
```

Keep `Platform=x64` consistent when building and launching. Node.js **22+** is needed for the JavaScript browser tooling and shared contract checks, not for the basic desktop launch above.

Before making a change:

- Read [AGENTS.md](AGENTS.md) for file ownership and verification rules.
- Keep changes focused; use theme dictionary tokens for UI styling and preserve keyboard access, accessible names, and High Contrast.
- For UI-only work, build and launch. For new backend logic, run the affected test project/filter once. Full-suite verification belongs to the integration phase.
- Include a clear description and relevant verification in your pull request. Keep generated outputs and private runtime data out of commits.

Example of a focused backend test command, after building the affected project:

```powershell
dotnet test tests/AriaGui.Core.Tests/AriaGui.Core.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TorrentManifestTests"
```

Use the class or filter relevant to your change. For bugs, include the app/source version, Windows version, steps to reproduce, and expected versus actual behavior in an [issue](https://github.com/servesh04/arai2-gui/issues). Remove private URLs and credentials from reports.

## 📚 Find your way around

| Start here | What it covers |
| --- | --- |
| [How it works](docs/HOW_IT_WORKS.md) | App layers, download flow, engine ownership, recovery, storage, and browser handoff. |
| [Developer guide](docs/DEVELOPMENT.md) | Toolchain, build profiles, debugging, diagnostics, and database migrations. |
| [Release notes](docs/RELEASE_NOTES.md) | The stable portable package and its verification limits. |
| [Release acceptance](docs/RELEASE_ACCEPTANCE.md) | Packaging and release acceptance requirements. |
| [Third-party record](docs/THIRD_PARTY.md) | Bundled components, notices, and redistribution review status. |
| [Implementation plan](IMPLEMENTATION_PLAN.md) | Original implementation phases and design context. |

## 🔧 Common questions

| Problem | Where to start |
| --- | --- |
| SDK cannot be found | Check `dotnet --list-sdks` against `global.json`. |
| Packaged launch requires Developer Mode | Use `.\scripts\Run.ps1 -Unpackaged`, or follow the packaged setup in the developer guide. |
| Build files are locked | Exit the running app through **Main menu → Help → Exit** before rebuilding. Closing its window leaves it running. |
| Engine cannot start | Check the engine status panel. AriaGUI uses local RPC port `16800`; another process using that port prevents startup. |
| Browser popup is disconnected | Confirm the host is registered, the app folder has not moved, and both sides have capture enabled. Follow the extension installation instructions. |
| Removing the portable folder leaves history behind | Settings and history live in per-user application data, separately from the executable. See [data storage](docs/HOW_IT_WORKS.md#data-storage). |

## 📜 License and credits

AriaGUI's original code is licensed under the [MIT License](LICENSE). Bundled components retain their own licenses; see the [third-party distribution record](docs/THIRD_PARTY.md) and release notes before distributing binaries.

AriaGUI uses [aria2](https://github.com/aria2/aria2) as a separate download-engine process. Its exact bundled version and upstream source are recorded in [vendor provenance](vendor/aria2/provenance.json), with license notices under `vendor/aria2/`. The interface uses Microsoft WinUI 3 and [Fluent UI System Icons](docs/design/assets/fluent/LICENSE).
