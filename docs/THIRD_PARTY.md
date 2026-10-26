# Third-party distribution record

This records the shipped-component notice review for the portable EXE ZIP. AriaGUI's original code is licensed under the MIT License in the repository root and portable ZIP. Bundled components retain their own licenses. This record is not legal advice or a claim that the release is authorized.

## aria2 executable

We bundle the unmodified official Windows x64 build 1 of aria2 1.37.0 as a separate child-process executable. The [pinned provenance](../vendor/aria2/provenance.json) records the exact asset, archive/executable hashes, and upstream source revision. Startup and package validation check the executable pin.

The portable ZIP includes COPYING, LICENSE.OpenSSL, AUTHORS, README.mingw, and provenance.json under Engine/Notices. The vendor COPYING is GPL v2; the [upstream license statement](https://aria2.github.io/#license) allows GPL v2 or later. The fact that the GUI talks over RPC does not remove the engine's redistribution obligations or decide the GUI's own license.

The ZIP carries the aria2 1.37.0 release source archive, its tagged Dockerfile.mingw and mingw-config, and the six statically linked library source archives under Source/aria2. Their URLs and SHA-256 values are pinned in provenance.json. Packaging verifies every download and archive; the ZIP validator rechecks their hashes against the pinned record. This is source-input verification, not a claim that a fresh build reproduces the upstream binary byte for byte. Retain the source folder with the executable when distributing the ZIP.

The upstream README.mingw identifies these statically linked versions:

| Component | Version |
| --- | --- |
| GMP | 6.3.0 |
| Expat | 2.5.0 |
| SQLite | 3.43.1 |
| zlib | 1.3 |
| c-ares | 1.19.1 |
| libssh2 | 1.11.0 |

The source bundle includes each listed archive and extracts its available license files into Notices/EngineStatic. SQLite's source archive has no core license file; its README is included there, and [SQLite identifies its deliverable code as public domain](https://www.sqlite.org/copyright.html). The bundle also includes the bundled wslay license from the aria2 source archive. The tagged mingw-config configures `--without-openssl`; LICENSE.OpenSSL remains with aria2's upstream notice files. The Windows build documents its TLS 1.3 limitation.

## .NET, Windows App SDK, SQLite and managed libraries

Self-contained distribution adds runtime binaries to our release. The packaging script copies the .NET runtime license/third-party notice files from the exact restored runtime package; package checks require their presence. The build uses the exact NuGet dependency locks checked into source. The package report records the embedded .NET version and engine pin.

The release dependency inventory records every resolved NuGet package's version, license metadata, copyright, and copied notice files. For MIT and Apache-2.0 expressions without a package license file, the ZIP includes pinned SPDX license texts under Notices/LicenseTexts; the inventory preserves each package's copyright attribution. Notices/Fluent-UI-System-Icons contains the icon license and notice. The ZIP validator classifies every shipped EXE and DLL into first-party, aria2, .NET/ASP.NET runtime, Windows App SDK, WebView2, SQLite, Entity Framework, or CommunityToolkit families and rejects an unreviewed binary. Build-only NuGet packages remain in the inventory but are not represented as shipped runtime binaries.

The notice review checks the exact portable ZIP contents. It does not cover MSI/MSIX, a browser-store extension, an independently repackaged EXE, or a future dependency update. Re-run the package validator after any change to bundled binaries or assets.

## Sources checked

- [aria2 1.37.0 source README](https://raw.githubusercontent.com/aria2/aria2/release-1.37.0/README.rst)
- [aria2 license in the pinned source](https://github.com/aria2/aria2/blob/release-1.37.0/COPYING)
- [GPL v2 text](https://www.gnu.org/licenses/old-licenses/gpl-2.0.html), also shipped in vendor/aria2/COPYING
- [Microsoft .NET runtime license](https://github.com/dotnet/runtime/blob/v10.0.9/LICENSE.TXT)
- [Windows App SDK license](https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE)

Status: **source inputs and shipped-component notices prepared for the portable ZIP**. AriaGUI's original code is MIT licensed; final release review remains pending.
