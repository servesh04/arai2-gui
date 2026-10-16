# AriaGUI 0.8.0 Beta 1

Prepared 16 October 2026. This is an unsigned public beta for Windows 11 x64.

## Install and run

Download `AriaGUI-0.8.0-beta.1-win-x64.zip`, verify its SHA-256 value against `SHA256SUMS.txt`, and extract the complete archive. Keep all files and subdirectories together, then run `AriaGui.App.exe`.

The portable build contains the required .NET, Windows App SDK, and pinned aria2 runtime. Close hides AriaGUI in the notification area; use **File > Exit** to stop the app and its managed aria2 process.

## Highlights

- Download management through the bundled aria2 1.37.0 engine, including queue control, pause and resume, retry, durable history, and interruption recovery.
- Responsive WinUI 3 interface with resizable panes and columns, compact layouts, search and status filters, transfer details, and tray operation.
- Saved aggregate upload/download bandwidth profiles, per-download caps, recurring weekly schedules, temporary overrides, and policy holds.
- Optional expected SHA-256 verification. A successful transfer is published only after its staged file matches the supplied digest.
- Guarded parallel HTTP downloading with fixed 2- or 4-connection choices. Parallel mode requires an expected SHA-256 and a compatible HTTP origin.
- Browser handoff foundations, including an open-only protocol path and native messaging components. Automatic browser interception is not part of this beta distribution.

## Verification completed

- The application test project passed 164/164 tests on the final M3 implementation.
- Partitioned Infrastructure runs cover every test class, including guarded downloads, recovery, bandwidth controls, browser handoff, checksum publication, and parallel range handling.
- Manual M3 checks completed byte-identical 8 MiB downloads using both 2 and 4 HTTP connections; the output SHA-256 values matched the source fixture.
- Manual 150% and 200% display-scaling checks, larger-text checks, high-contrast checks, and the responsive Add dialog check passed.

## Beta limitations

- The ZIP and executable are unsigned. Windows may display an unknown-publisher warning.
- Clean-machine installer/MSIX acceptance and production code signing are not complete. This beta is distributed as a portable ZIP.
- Parallel HTTP mode supports eligible single-file HTTP/HTTPS downloads with a stable representation, byte ranges, and a known expected SHA-256. Unsupported servers must use one connection.
- Narrator speech, mixed-DPI movement between monitors, ARM64, and Windows versions earlier than Windows 11 build 22000 are not certified.
- The browser extension and automatic browser-download interception are not included in this release.
- A single monolithic Infrastructure test invocation was not completed because of testhost/runtime duration issues; all Infrastructure test classes passed in bounded partitions.

## Data and compatibility

The default download directory is `Downloads\AriaGUI`. Application settings, history, sessions, and private recovery state are stored under the current user's local application data. Removing the portable folder does not remove those per-user files or completed downloads.

Do not run packaged and portable AriaGUI builds at the same time because both use the same private aria2 RPC port. Back up important partial downloads and application data before moving between prerelease builds.

## Integrity

The release includes `SHA256SUMS.txt` with hashes for the ZIP and primary executable. GitHub also publishes source archives for the tagged source commit.
