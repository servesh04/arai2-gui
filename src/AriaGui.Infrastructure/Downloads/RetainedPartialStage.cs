using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.Infrastructure.Downloads;

public sealed record RetainedPartialStage(string Directory, string FileName)
{
    // Originals remain immutable; no publication method is provided before crash-safe pair
    // publication, engine ownership and user destination collision rules are implemented.
    public static async Task<RetainedPartialStage> CreateAsync(string privateStageRoot, string retainedPath, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(privateStageRoot) || !Path.IsPathFullyQualified(retainedPath)) throw new ArgumentException();
        var name = Path.GetFileName(retainedPath);
        if (!WindowsFileNamePolicy.IsValid(name)) throw new ArgumentException();
        foreach (var path in new[] { retainedPath, retainedPath + ".aria2" })
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Retained payload/control pair required.");
        var root = Path.GetFullPath(privateStageRoot);
        UserRuntimeDirectory.EnsurePrivate(root);
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        // FileShare.Read prevents copying a pair which is still writable by an engine.
        await using var payload = new FileStream(retainedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true);
        await using var control = new FileStream(retainedPath + ".aria2", FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true);
        foreach (var pair in new[] { (payload, name), (control, name + ".aria2") })
        {
            await using var destination = new FileStream(Path.Combine(directory, pair.Item2), FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, FileOptions.WriteThrough);
            await pair.Item1.CopyToAsync(destination, token); await destination.FlushAsync(token); destination.Flush(true);
        }
        return new(directory, name);
    }
}
