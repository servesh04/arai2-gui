using System.Text.Json;

namespace AriaGui.Browser.Spikes;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "browser-probe")
            {
                var evidence = await BrowserSpike.ProbeAsync(args[1], args[2]);
                Console.WriteLine(JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true })); return 0;
            }
            if (args.Length > 0 && args[0] == "probe")
            {
                if (args.Length != 2) throw new ArgumentException("Supply a new empty artifact directory.");
                var root = Path.GetFullPath(args[1]);
                if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                    throw new InvalidOperationException("Artifact directory must be empty.");
                Directory.CreateDirectory(root);
                var results = new List<object>();
                foreach (var (name, run) in new (string, Func<string, Task<object>>)[]
                {
                    ("native-framing", NativeSpike.ProbeAsync), ("private-pipe", PipeSpike.ProbeAsync),
                    ("receipt-transaction", ReceiptSpike.ProbeAsync), ("attachment", AttachmentSpike.ProbeAsync)
                })
                {
                    try { results.Add(new { name, status = "passed", evidence = await run(root) }); }
                    catch (Exception ex)
                    {
                        results.Add(new
                        {
                            name,
                            status = "failed",
                            errorType = ex.GetType().Name,
                            hresult = ex.HResult,
                            nativeError = ex is System.ComponentModel.Win32Exception windows ? windows.NativeErrorCode : (int?)null
                        });
                    }
                }
                var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(root, "results.json"), json);
                Console.WriteLine(json);
                return results.Any(r => JsonSerializer.Serialize(r).Contains("\"status\":\"failed\"", StringComparison.Ordinal)) ? 1 : 0;
            }
            if (args.Length == 3 && args[0] == "pipe-client")
            {
                await PipeSpike.ClientAsync(args[1], args[2]); return 0;
            }
            // Default executable entry point is also usable by a throwaway Chromium extension.
            return await NativeSpike.HostAsync(args);
        }
        catch (Exception ex)
        {
            // No input/URL/exception message is copied to stdout or stderr.
            Console.Error.WriteLine($"spike_error:{ex.GetType().Name}"); return 2;
        }
    }
    internal static void Check(bool condition, string invariant)
    { if (!condition) throw new InvalidOperationException(invariant); }
}
