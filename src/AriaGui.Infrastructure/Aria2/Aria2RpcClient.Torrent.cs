using System.Globalization;
using System.Text.Json;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;

namespace AriaGui.Infrastructure.Aria2;

public sealed record TorrentEngineObservation(string Gid, TorrentStage Stage, long CompletedBytes,
    long TotalBytes, long DownloadBytesPerSecond, long VerifiedBytes, string? Error = null);

public sealed partial class Aria2RpcClient
{
    public async Task RemoveFailedTorrentResultAsync(string gid, CancellationToken token)
    {
        if (!IsGid(gid)) throw new ArgumentException("Invalid torrent engine identity.", nameof(gid));
        if (await CallAsync<string>("aria2.removeDownloadResult", token, [gid]).ConfigureAwait(false) != "OK")
            throw InvalidResponse();
    }
    private static readonly string[] TorrentKeys =
    [
        "gid", "status", "infoHash", "dir", "files", "totalLength", "completedLength",
        "downloadSpeed", "verifiedLength", "verifyIntegrityPending", "errorCode"
    ];

    public async Task<TorrentEngineObservation> AddTorrentAsync(byte[] metainfo, TorrentManifest manifest,
        TorrentSelection selection, string payloadRoot, string gid, CancellationToken token)
    {
        if (metainfo.Length is 0 or > TorrentManifestParser.MaximumMetadataBytes
            || TorrentManifestParser.Parse(metainfo, token).InfoHash != manifest.InfoHash
            || !IsGid(gid) || !Path.IsPathFullyQualified(payloadRoot)
            || selection.FileIndices.Count == 0 || selection.FileIndices.Any(index => index < 1 || index > manifest.Files.Count))
            throw new EngineException(EngineFailure.RequestRejected, "Saved torrent dispatch intent is invalid.");
        var options = new Dictionary<string, string>
        {
            ["gid"] = gid,
            ["dir"] = Path.GetFullPath(payloadRoot),
            ["select-file"] = string.Join(',', selection.FileIndices.Order()),
            ["pause"] = "true",
            ["continue"] = "true",
            ["check-integrity"] = "true",
            ["seed-time"] = "0",
            ["max-upload-limit"] = "64K",
            ["bt-max-peers"] = "40",
            ["enable-peer-exchange"] = "false",
            ["bt-enable-lpd"] = "false",
            ["bt-remove-unselected-file"] = "false",
            ["allow-overwrite"] = "false",
            ["auto-file-renaming"] = "false",
            ["rpc-save-upload-metadata"] = "true",
            ["max-connection-per-server"] = "4"
        };
        var result = await CallAsync<string>("aria2.addTorrent", token,
            [Convert.ToBase64String(metainfo), Array.Empty<string>(), options]).ConfigureAwait(false);
        if (result != gid) throw InvalidResponse();
        return await GetTorrentAsync(gid, manifest, selection, payloadRoot, token).ConfigureAwait(false)
            ?? throw InvalidResponse();
    }

    public async Task<TorrentEngineObservation?> GetTorrentAsync(string gid, TorrentManifest manifest,
        TorrentSelection selection, string payloadRoot, CancellationToken token)
    {
        JsonElement status;
        try { status = await CallAsync<JsonElement>("aria2.tellStatus", token, [gid, TorrentKeys]).ConfigureAwait(false); }
        catch (EngineException ex) when (ex.Failure == EngineFailure.TransferNotFound) { return null; }
        if (status.ValueKind != JsonValueKind.Object || !Text(status, "gid").Equals(gid, StringComparison.Ordinal)
            || !Text(status, "infoHash").Equals(manifest.InfoHash, StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(payloadRoot)
            || !SamePath(Text(status, "dir"), payloadRoot)
            || !status.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
            || files.GetArrayLength() != manifest.Files.Count)
            throw new EngineException(EngineFailure.RequestRejected, "Engine torrent identity differs from the saved draft.");
        var selected = selection.FileIndices.ToHashSet();
        var seen = new HashSet<int>();
        foreach (var file in files.EnumerateArray())
        {
            if (!int.TryParse(Text(file, "index"), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 1 || index > manifest.Files.Count || !seen.Add(index)
                || !SamePath(Text(file, "path"), Path.Combine(payloadRoot, manifest.Files[index - 1].RelativePath))
                || Number(file, "length") != manifest.Files[index - 1].Length
                || Text(file, "selected") != (selected.Contains(index) ? "true" : "false"))
                throw new EngineException(EngineFailure.RequestRejected, "Engine torrent files differ from the saved selection.");
        }
        var state = Text(status, "status");
        var stage = state switch
        {
            "waiting" => TorrentStage.Waiting,
            "paused" => TorrentStage.Stopped,
            "active" when status.TryGetProperty("verifiedLength", out _) ||
                Text(status, "verifyIntegrityPending", optional: true) == "true" => TorrentStage.Checking,
            "active" => TorrentStage.Downloading,
            "complete" => TorrentStage.Publishing,
            "error" => TorrentStage.Failed,
            "removed" => TorrentStage.Stopped,
            _ => throw InvalidResponse()
        };
        var completed = Number(status, "completedLength");
        var total = Number(status, "totalLength");
        if (total == 0 && stage is (TorrentStage.Waiting or TorrentStage.Stopped))
            total = selection.SelectedBytes;
        if (completed > total) throw InvalidResponse();
        return new(gid, stage, completed, total, Number(status, "downloadSpeed"),
            Number(status, "verifiedLength", optional: true), stage == TorrentStage.Failed ? "Torrent transfer failed." : null);
    }

    private static string Text(JsonElement element, string key, bool optional = false)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (optional) return string.Empty;
        throw InvalidResponse();
    }
    private static long Number(JsonElement element, string key, bool optional = false)
    {
        var value = Text(element, key, optional);
        if (optional && value.Length == 0) return 0;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number : throw InvalidResponse();
    }
    private static bool SamePath(string first, string second)
    {
        try { return Path.IsPathFullyQualified(first) && Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static bool IsGid(string gid) => gid.Length == 16 && gid != "0000000000000000" && gid.All(Uri.IsHexDigit);
}
