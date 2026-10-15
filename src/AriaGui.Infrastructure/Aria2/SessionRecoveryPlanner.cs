using System.Text;
using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Aria2;

internal sealed record SessionRecoveryPlan(IReadOnlyList<DownloadEntry> Entries, string? InputFile, string Message);
internal sealed record SessionCheckpoint(string SessionFile);

// Imported session options are never executed. Only matching identities from the database
// are translated into a fresh, restricted input file, and every restored item starts paused.
internal static class SessionRecoveryPlanner
{
    public static async Task<SessionRecoveryPlan> PrepareAsync(string runtime, IReadOnlyList<DownloadEntry> history, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var sessions = Path.Combine(runtime, "sessions");
            Directory.CreateDirectory(sessions);
            var checkpoint = Path.Combine(runtime, "engine.checkpoint.json");
            string? source;
            try
            {
                source = File.Exists(checkpoint)
                    ? JsonSerializer.Deserialize<SessionCheckpoint>(ReadBounded(checkpoint))?.SessionFile
                    : Directory.EnumerateFiles(sessions, "session-*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (source is null) return new SessionRecoveryPlan([], null, "No saved engine session; missing transfers need manual recovery.");
                if (!IsSessionPath(runtime, source) || !File.Exists(source))
                    return new SessionRecoveryPlan([], null, "Saved session is missing or outside the private session folder; recovery requires review.");
                var records = Parse(ReadBounded(source));
                var known = history.ToDictionary(row => row.Attempt.EngineId, StringComparer.Ordinal);
                var restore = new List<DownloadEntry>();
                var unknown = 0; var blocked = 0;
                foreach (var record in records)
                {
                    if (!known.TryGetValue(record.Gid, out var entry)) { unknown++; continue; }
                    if (entry.Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Removed || entry.DesiredRemoved) continue;
                    // A direct engine publishes bytes itself; it cannot uphold a
                    // required pre-publication checksum after restart.
                    if (entry.Job.ExpectedChecksum is not null || record.Source != (entry.ExactSourceUrl ?? entry.Job.Source.AbsoluteUri) || !CanRestore(entry)) { blocked++; continue; }
                    restore.Add(entry);
                }
                // POS_SET intent can be reapplied safely only after the old process is gone.
                foreach (var entry in restore.Where(row => row.PendingOperation?.Action == DownloadAction.Move).ToArray())
                {
                    restore.Remove(entry);
                    restore.Insert(Math.Clamp(entry.PendingOperation!.QueuePosition ?? 0, 0, restore.Count), entry);
                }
                string? input = null;
                if (restore.Count > 0)
                {
                    input = Path.Combine(sessions, $"restore-{Guid.NewGuid():N}.txt");
                    File.WriteAllText(input, string.Join("\n", restore.Select(Format)), new UTF8Encoding(false));
                }
                return new SessionRecoveryPlan(restore, input, $"Restored {restore.Count} known transfers; {blocked} require review; {unknown} unrecognized session entries preserved.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            { return new SessionRecoveryPlan([], null, "Saved session could not be validated. It was preserved; transfers need manual recovery."); }
        }, token).ConfigureAwait(false);
    }

    public static bool IsSessionPath(string runtime, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var root = Path.GetFullPath(Path.Combine(runtime, "sessions")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0);
    }
    private static string ReadBounded(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Oversized recovery metadata.");
        return File.ReadAllText(path);
    }
    private static List<(string Source, string Gid)> Parse(string text)
    {
        var result = new List<(string, string)>();
        string? source = null, gid = null;
        void Flush()
        {
            if (source is null) return;
            if (gid is null || gid.Length != 16 || !gid.All(Uri.IsHexDigit) || gid == "0000000000000000") throw new InvalidDataException();
            result.Add((source, gid.ToLowerInvariant()));
        }
        foreach (var line in text.Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(clean) || clean.StartsWith('#')) continue;
            if (!char.IsWhiteSpace(clean[0])) { Flush(); source = clean.TrimEnd('\t', ' '); gid = null; }
            else if (clean.TrimStart().StartsWith("gid=", StringComparison.Ordinal)) gid = clean.Trim()[4..];
        }
        Flush();
        if (result.Select(row => row.Item2).Distinct(StringComparer.Ordinal).Count() != result.Count) throw new InvalidDataException();
        return result;
    }
    private static bool CanRestore(DownloadEntry entry)
    {
        var folder = Path.GetDirectoryName(entry.Job.Destination);
        return folder is not null && Path.IsPathFullyQualified(folder) && Directory.Exists(folder)
            && folder.IndexOfAny(['\r', '\n']) < 0 && FileDownloadDestination.IsValidName(entry.File.FileName)
            && string.Equals(Path.Combine(folder, entry.File.FileName), entry.Job.Destination, StringComparison.OrdinalIgnoreCase)
            && entry.Job.Source.Scheme is "http" or "https" && string.IsNullOrEmpty(entry.Job.Source.UserInfo)
            && !Directory.Exists(entry.Job.Destination) && !Directory.Exists(entry.Job.Destination + ".aria2")
            && (!File.Exists(entry.Job.Destination) || File.Exists(entry.Job.Destination + ".aria2"));
    }
    private static string Format(DownloadEntry entry) => string.Join("\n", new[]
    {
        entry.ExactSourceUrl ?? entry.Job.Source.AbsoluteUri, $" gid={entry.Attempt.EngineId}", $" dir={Path.GetDirectoryName(entry.Job.Destination)}",
        $" out={entry.File.FileName}", " pause=true", " continue=false", " always-resume=true", " allow-overwrite=false",
        " auto-file-renaming=false", " content-disposition=false", " follow-torrent=false", " follow-metalink=false",
        " check-certificate=true", " split=1", " max-connection-per-server=1", ""
    });
}
