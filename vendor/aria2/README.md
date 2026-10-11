# Bundled aria2 engine

The Windows x64 executable is the unmodified `aria2c.exe` from the official [aria2 1.37.0 release](https://github.com/aria2/aria2/releases/tag/release-1.37.0), Windows build 1. `provenance.json` records the download/source URLs and SHA-256 values measured when acquiring it. These pins detect a changed download or executable; they are not an upstream signature verification.

The app copies the executable to `Engine/aria2c.exe` and the release's four notice/build-information files plus provenance to `Engine/Notices`. Keep those files with the binary. `COPYING` contains the GPL license; `LICENSE.OpenSSL` contains the release's OpenSSL notice. Phase 7's local package verifies these files, but complete redistribution and corresponding-source delivery remain pending before public distribution; see [the review record](../../docs/THIRD_PARTY.md).

`README.mingw` records the upstream build and dependencies. It also notes that TLS 1.3 does not work in this Windows build; test HTTPS compatibility during Phase 3 and reconsider the engine build before release if necessary.

Normally no download is needed: the engine is checked into this project's vendor folder. To reacquire the exact asset after a missing/corrupt checkout, run from the project root:

```powershell
.\scripts\Get-Aria2.ps1
```

The script verifies both hashes before replacing vendor files. Updating aria2 deliberately requires reviewing the upstream release and updating both `provenance.json` and `Aria2ProcessOptions` pins, then rerunning real-engine tests. The process manager checks the executable hash on every startup.
