using System.Text.Json;
using System.Text.RegularExpressions;

// Test-only shell argument capture. The sidecar confines receipts to an owned directory
// and a fresh synthetic scheme; no production app or arbitrary URI logging is involved.
var root = AppContext.BaseDirectory;
if (!File.Exists(Path.Combine(root, "synthetic-open-fixture"))) return 91;
// Bounded process fixture for installer busy checks; emits no receipt/runtime mutation.
if (args is ["--synthetic-hold"])
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}
var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Path.Combine(root, "fixture.json")))!;
var permittedScheme = Regex.IsMatch(config.Scheme, "^ariagui-open-fixture-[a-f0-9]{32}$") ||
    (config.Scheme == "ariagui" && File.Exists(Path.Combine(root, "synthetic-canonical-delivery")));
if (!permittedScheme || args.Length is < 1 or > 4 || args.Any(arg => arg.Length > 1024) ||
    !args[0].StartsWith(config.Scheme + ":", StringComparison.OrdinalIgnoreCase)) return 92;
var receipt = new { SchemaVersion = 1, ProcessId = Environment.ProcessId, Executable = Environment.ProcessPath,
    Arguments = args, ExitCode = config.ExitCode };
File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(receipt));
return config.ExitCode;

sealed record Configuration(string Scheme, int ExitCode);
