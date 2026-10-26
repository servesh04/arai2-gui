# AriaGUI 1.0.0

Prepared 26 October 2026. This is the first stable portable EXE release for Windows 11 x64.

## Install and run

Download `AriaGUI-1.0.0-win-x64.zip` and `SHA256SUMS.txt` from the same release. Verify the ZIP checksum, extract the complete archive, keep its files and subdirectories together, then run `AriaGui.App.exe`.

The portable build contains the required .NET and Windows App SDK runtimes and the pinned aria2 1.37.0 engine. Closing the window keeps AriaGUI in the tray; use **Main menu > Help > Exit** to stop the app and its engine.

## Included features

- HTTP/HTTPS downloads with queue control, pause/resume, retry, status filters, history, transfer details, bandwidth controls, and recovery after interruption.
- Local BitTorrent v1 `.torrent` intake with per-file selection, selected-file progress, details, publication, pause/resume, and recovery. Seeding stops when the download finishes. DHT, peer exchange, and local peer discovery are disabled; trackerless torrents may not find peers.
- Desktop notifications and the in-app notification center for transfer events.
- An optional Chrome/Edge browser handoff through the separately distributed, unpacked extension and native host. Both sides require opt-in; if the app declines a handoff, the browser keeps its download. The extension is not a Web Store release and does not update automatically.
- A responsive WinUI interface with resizable details pane, light/dark themes, High Contrast resources, and keyboard access.

## Scope and known limits

- The portable ZIP and EXE are unsigned. Windows may show an unknown-publisher warning. MSI/MSIX and production code signing are deferred.
- Magnet links, BitTorrent v2/hybrid files, configurable seeding, public torrent search, and browser-store distribution are not included.
- Torrent peers can see your IP address. VPN or proxy settings may not cover peer traffic; Windows Firewall may ask about aria2. Selected torrent files are staged in private storage and copied to the destination after verification, so sufficient free space is needed for both copies.
- ARM64, Windows versions below build 22000, Narrator speech, and mixed-DPI movement between monitors are not certified. A Windows 11 clean-machine acceptance run remains pending.
- Guarded parallel HTTP mode supports eligible single-file transfers with an expected SHA-256 and a compatible range-capable origin. Other transfers use one connection.

## Data and compatibility

The default download directory is `Downloads\AriaGUI`. Application settings, history, sessions, and private recovery state are stored under the current user's local application data. Removing the portable folder does not remove those per-user files or completed downloads.

Do not run packaged and portable AriaGUI builds at the same time because both use the same private aria2 RPC port. Back up important partial downloads and application data before replacing an earlier build.

## Verification and redistribution

The portable package validator checks the bundled engine, runtimes and dependency notices, source inputs for aria2 and its statically linked libraries, absence of private files, and the root MIT license. Verify the exact `1.0.0` CI artifact and its `SHA256SUMS.txt` before publishing; earlier beta results do not verify this build.

AriaGUI's original code is MIT licensed. Bundled aria2 and other components retain their own licenses and notices in the ZIP. See `LICENSE`, `Engine/Notices`, `Notices`, `Source/aria2`, and the repository's third-party distribution record.
