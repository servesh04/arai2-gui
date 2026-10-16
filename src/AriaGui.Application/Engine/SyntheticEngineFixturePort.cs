using System.Globalization;

namespace AriaGui.Application.Engine;

// Consumed only by marked DEBUG/verification app composition and the synthetic helper.
// No URI/CLI argument selects this setting. Normal Release composition never reads it.
public static class SyntheticEngineFixturePort
{
    public const string FileName = "phase-c-private-rpc-port.txt";
    public static int Read(string profile)
    {
        if (!File.Exists(Path.Combine(profile, "synthetic-r1-fixture"))) throw new InvalidOperationException("Synthetic profile marker required.");
        var path = Path.Combine(profile, FileName);
        if (!File.Exists(path)) return 16800;
        if (new FileInfo(path).Length is < 1 or > 5) throw new InvalidOperationException("Invalid synthetic RPC port.");
        var value = File.ReadAllText(path);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 49152 or > 65535)
            throw new InvalidOperationException("Invalid synthetic RPC port.");
        return port;
    }
}
