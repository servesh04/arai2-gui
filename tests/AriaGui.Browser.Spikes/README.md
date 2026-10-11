# Package 0 executable spikes

These are disposable design probes, excluded from the application solution and release packaging. They implement no add API, native-host installation, app listener, production migration, browser settings or credentials.

Build serially, then run the generated apphost with `probe` and a **new empty** artifact directory:

```powershell
dotnet restore tests/AriaGui.Browser.Spikes/AriaGui.Browser.Spikes.csproj -p:NuGetAudit=false
dotnet build tests/AriaGui.Browser.Spikes/AriaGui.Browser.Spikes.csproj --no-restore -m:1 -nr:false
& tests/AriaGui.Browser.Spikes/bin/Debug/net10.0/AriaGui.Browser.Spikes.exe probe artifacts/browser-wp0/run-new
```

Use an ordinary Windows user execution context that permits local named pipes and VSTest sockets. The default probe starts only its own apphost children, creates private synthetic SQLite state and a harmless NTFS text attachment, and leaves artifacts for inspection. Every transport/process wait is bounded. The default probe never launches aria2 or a browser, changes registry keys, opens a file, or touches production profiles.

Restricted sandbox execution can deny the child pipe connection. Such a run stays failed; `pipe-diagnostics.json` records the parent stage, child exit and fixed diagnostic tokens. The harness observes early child exit instead of waiting for a misleading connection timeout, and kills only its own child on timeout. Do not relax the protected current-user ACL to force a restricted-context pass.

The adjacent Chromium manifest has a fixed development public key. Its SHA256-derived extension ID is used both by host caller checks and the generated native manifest. The process proof supplies the documented Chromium origin and parent-window arguments, but this is **not browser authorization evidence**. Only `hello` returns `spike_ready` with no feature capabilities; it does not claim GUI readiness.

The explicit `browser-probe <ChromiumExe> <new-empty-directory>` mode launches a headless synthetic profile and extension. It checks uniquely generated HKCU Chromium/Chrome development host keys are unused, registers its own manifest, waits at most 25 seconds for an extension console marker, and removes only keys whose contents still match its manifest in finally. It terminates only its own browser process tree. No machine-wide or personal profile installation occurs. Chrome branded137+ does not support the command-line `--load-extension` route. Firefox requires its own manifest/invocation adapter; this spike does not pretend to support it.

The receipt spike uses the current production EF migrations and row types in an isolated database plus a clearly named experimental table. It proves transaction/concurrency behavior and calls the real coordinator's restart reconciliation. It does **not** implement external intake, canonical payload hashing, a production ledger FK/tombstone migration or reliable never-sent dispatch recovery.
