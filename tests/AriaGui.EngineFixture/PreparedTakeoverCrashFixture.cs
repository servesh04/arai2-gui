using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Infrastructure.Persistence;

internal static class PreparedTakeoverCrashFixture
{
    internal static async Task<int> RunAsync(string root, string boundary)
    {
        root = Path.GetFullPath(root);
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.TakeoverTests")) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(root, "crash-packet.json"))) throw new ArgumentException();
        if (boundary is not ("prepare-before-commit" or "prepare-after-commit" or "commit-before-commit" or "commit-after-commit" or "abort-before-commit" or "abort-after-commit")) throw new ArgumentException();
        var packet = JsonSerializer.Deserialize<Packet>(await File.ReadAllTextAsync(Path.Combine(root, "crash-packet.json")))!;
        var repository = new SqliteDownloadRepository(Path.Combine(root, "downloads.db"), takeoverClock: new Clock(packet.Now), takeoverHooks: new(stage =>
        {
            if (stage != boundary) return;
            Console.WriteLine("BOUNDARY"); Console.Out.Flush();
            new ManualResetEventSlim(false).Wait(); // Parent terminates process abruptly here.
        }));
        await repository.InitializeAsync(default);
        if (boundary.StartsWith("prepare", StringComparison.Ordinal)) await repository.PrepareTakeoverAsync(packet.Request, packet.Digest, packet.Candidate, default);
        else if (boundary.StartsWith("commit", StringComparison.Ordinal)) await repository.CommitTakeoverAsync(packet.Request.Key, packet.Digest, default);
        else await repository.AbortTakeoverAsync(packet.Request.Key, default);
        return 2; // Reaching here means requested boundary was not exercised.
    }
    private sealed record Packet(ExternalDownloadRequest Request, string Digest, DownloadEntry Candidate, DateTimeOffset Now);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
