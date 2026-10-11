# Third-party distribution record

This is a review record, not a claim that public redistribution is ready.

## aria2 executable

We bundle the unmodified official Windows x64 build 1 of aria2 1.37.0 as a separate child-process executable. The [pinned provenance](../vendor/aria2/provenance.json) records the exact asset, archive/executable hashes, and upstream source revision. Startup and package validation check the executable pin.

The MSIX includes COPYING, LICENSE.OpenSSL, AUTHORS, README.mingw, and provenance.json under Engine/Notices. The vendor COPYING is GPL v2; the [upstream license statement](https://aria2.github.io/#license) allows GPL v2 or later. Verify the linked-library exceptions/terms against the exact Windows build during the corresponding-source review. The fact that the GUI talks over RPC does not remove the engine's redistribution obligations or decide the GUI's own license.

Before public binary distribution, prepare and verify the applicable corresponding-source delivery (or a valid source offer) for the shipped engine and included code. A GitHub link alone is not our completed source-delivery plan. Match the Windows build scripts, configuration, patches, and statically linked libraries to this exact executable; retain required notices and any source/offer alongside the release. Review the GUI's chosen license separately; no project license has been invented here.

The upstream README.mingw identifies these statically linked versions:

| Component | Version |
| --- | --- |
| GMP | 6.3.0 |
| Expat | 2.5.0 |
| SQLite | 3.43.1 |
| zlib | 1.3 |
| c-ares | 1.19.1 |
| libssh2 | 1.11.0 |

This table identifies source/notice work to complete; it does not assert that the four bundled notice files cover all those components. The Windows build also documents its TLS 1.3 limitation.

## .NET, Windows App SDK, SQLite and managed libraries

Self-contained distribution adds runtime binaries to our release. The packaging script copies the .NET runtime license/third-party notice files from the exact restored runtime package; package checks require their presence. The build uses the exact NuGet dependency locks checked into source. The package report records the embedded .NET version and engine pin.

The release dependency inventory records every resolved NuGet build dependency with its declared license metadata. It is a starting point for the runtime/content notice review, not an automatically complete license bundle: build-only packages and included native payloads require distinguishing before public redistribution. Include required Windows App SDK, SQLite and managed-library license/notices and satisfy their distribution terms before publishing.

## Sources checked

- [aria2 1.37.0 source README](https://raw.githubusercontent.com/aria2/aria2/release-1.37.0/README.rst)
- [aria2 license in the pinned source](https://github.com/aria2/aria2/blob/release-1.37.0/COPYING)
- [GPL v2 text](https://www.gnu.org/licenses/old-licenses/gpl-2.0.html), also shipped in vendor/aria2/COPYING
- [Microsoft .NET runtime license](https://github.com/dotnet/runtime/blob/v10.0.9/LICENSE.TXT)
- [Windows App SDK license](https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE)

Status: **not cleared for public redistribution**. Local package creation and development signing do not clear this gate.
