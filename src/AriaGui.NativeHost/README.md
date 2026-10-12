# AriaGui.NativeHost

Windows x64 native-messaging console host; no GUI/EF references. The installer supplies the fixed adjacent `native-host.v1.json` and private runtime installation UUID. Missing or invalid configuration fails closed. No registry/browser installation is supplied by this project.

Build: `dotnet build src/AriaGui.NativeHost/AriaGui.NativeHost.csproj -m:1 -nr:false`.

Self-contained Windows x64 packaging: `dotnet publish src/AriaGui.NativeHost/AriaGui.NativeHost.csproj -c Release -r win-x64 --self-contained true -m:1 -nr:false -o artifacts/browser-wp4/native-host-win-x64`. RID publishing updates generated restore targets; restore the normal solution lock shape before ordinary locked verification. The published output has no descriptor/registration and cannot enable production browser traffic. Verification descriptors are accepted only by Debug builds.

Run `NativeHostProcessTests` in Infrastructure.Tests for private synthetic process tests. Standard Verify also requires Node.js 22+ for shared v1 fixture compatibility. See [Package 4 evidence](../../docs/BROWSER_HANDOFF_WP4_EVIDENCE.md) for descriptor fields, deadlines, outcome semantics and release gates.
