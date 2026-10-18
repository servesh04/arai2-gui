using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AriaGui.Application.Engine;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using System.Globalization;

namespace AriaGui.Infrastructure.Aria2;

public sealed class Aria2RpcClient(HttpClient http, Uri endpoint, string secret, TimeSpan requestTimeout)
{
    private long _requestId;
    internal string OwnershipFailure { get; private set; } = "none";
    private static readonly string[] ProgressKeys = ["gid", "status", "totalLength", "completedLength", "downloadSpeed", "errorCode", "errorMessage"];

    public async Task<EngineInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var version = await CallAsync<VersionResponse>("aria2.getVersion", cancellationToken).ConfigureAwait(false);
        var session = await CallAsync<SessionResponse>("aria2.getSessionInfo", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(version.Version) || string.IsNullOrWhiteSpace(session.SessionId))
            throw InvalidResponse();
        return new(version.Version, session.SessionId);
    }

    public Task<string> SaveSessionAsync(CancellationToken cancellationToken = default) =>
        CallAsync<string>("aria2.saveSession", cancellationToken);
    public Task<string> ShutdownAsync(CancellationToken cancellationToken = default) =>
        CallAsync<string>("aria2.shutdown", cancellationToken);
    internal async Task<bool> MatchesTransferOwnershipAsync(string gid, string source, string path, CancellationToken token)
    {
        var uris = await CallAsync<JsonElement[]>("aria2.getUris", token, [gid]).ConfigureAwait(false);
        var files = await CallAsync<JsonElement[]>("aria2.getFiles", token, [gid]).ConfigureAwait(false);
        OwnershipFailure = "uri-count-" + uris.Length;
        if (uris.Length is < 1 or > 16) return false;
        OwnershipFailure = "uri-value";
        if (uris.Any(value => value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String || uri.GetString() != source)) return false;
        OwnershipFailure = "file-count";
        if (files.Length != 1) return false;
        OwnershipFailure = "file-path";
        if (files[0].ValueKind != JsonValueKind.Object || !files[0].TryGetProperty("path", out var file) || file.ValueKind != JsonValueKind.String || file.GetString() is not string observed || !Path.IsPathFullyQualified(observed)
            || !string.Equals(Path.GetFullPath(observed), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return false;
        OwnershipFailure = "none"; return true;
    }
    internal async Task<bool> MatchesStoppedOwnershipAsync(string gid, string source, string path, CancellationToken token)
    {
        // getUris is unavailable for pinned aria2 stopped results. tellStatus includes
        // authenticated terminal state and the same files/URI structures.
        var result = await CallAsync<JsonElement>("aria2.tellStatus", token, [gid, new[] { "gid", "status", "files" }]);
        OwnershipFailure = "stopped-identity";
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("gid", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != gid
            || !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() is not ("complete" or "error" or "removed")
            || !result.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 1) return false;
        var file = files[0]; OwnershipFailure = "stopped-file";
        if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("path", out var observed) || observed.ValueKind != JsonValueKind.String
            || observed.GetString() is not string value || !Path.IsPathFullyQualified(value) || !string.Equals(Path.GetFullPath(value), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            || !file.TryGetProperty("uris", out var uris) || uris.ValueKind != JsonValueKind.Array || uris.GetArrayLength() is < 1 or > 16) return false;
        OwnershipFailure = "stopped-uri";
        foreach (var uri in uris.EnumerateArray())
            if (uri.ValueKind != JsonValueKind.Object || !uri.TryGetProperty("uri", out var address) || address.ValueKind != JsonValueKind.String || address.GetString() != source) return false;
        OwnershipFailure = "none"; return true;
    }

    public async Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
    {
        if (request.HttpConnections is not (1 or 2 or 4)) throw new EngineException(EngineFailure.RequestRejected, "Unsupported HTTP connection count.");
        if (request.HttpConnections > 1 && (request.ExpectedChecksum is null || request.Source.IsLoopback == false ||
            !request.Source.AbsolutePath.StartsWith("/v2/transfer/", StringComparison.Ordinal)))
            throw new EngineException(EngineFailure.RequestRejected, "Parallel HTTP requires an authenticated v2 gateway route and expected SHA-256.");
        var options = new Dictionary<string, string>
        {
            ["gid"] = request.EngineId, ["dir"] = request.Directory, ["out"] = request.FileName,
            ["allow-overwrite"] = "false", ["auto-file-renaming"] = "false", ["continue"] = request.ContinueExisting ? "true" : "false",
            ["always-resume"] = "true",
            ["pause"] = request.StartPaused ? "true" : "false",
            ["content-disposition"] = "false", ["follow-torrent"] = "false", ["follow-metalink"] = "false",
            ["check-certificate"] = "true", ["split"] = request.HttpConnections.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["max-connection-per-server"] = request.HttpConnections.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (request.HttpConnections > 1) options["min-split-size"] = "1M";
        if (request.JobDownloadLimit is { } cap)
        {
            cap.Validate();
            options["max-download-limit"] = (cap.BytesPerSecond ?? 0).ToString(CultureInfo.InvariantCulture);
        }
        var gid = await CallAsync<string>("aria2.addUri", token, [new[] { request.ExactSourceUrl ?? request.Source.AbsoluteUri }, options]).ConfigureAwait(false);
        if (gid != request.EngineId) throw InvalidResponse();
        return await FindAsync(gid, token).ConfigureAwait(false) ?? throw InvalidResponse();
    }

    public async Task<EngineDownload?> FindAsync(string gid, CancellationToken token)
    {
        TransferResponse transfer;
        try
        {
            transfer = await CallAsync<TransferResponse>("aria2.tellStatus", token,
                [gid, ProgressKeys]).ConfigureAwait(false);
        }
        catch (EngineException ex) when (ex.Failure == EngineFailure.TransferNotFound) { return null; }
        if (transfer.Gid != gid) throw InvalidResponse();
        return Map(transfer);
    }

    public async Task<IReadOnlyList<EngineDownload>> FindManyAsync(IReadOnlyList<string> gids, CancellationToken token)
    {
        var result = new List<EngineDownload>();
        foreach (var chunk in gids.Chunk(64))
        {
            var calls = chunk.Select(gid => new { methodName = "aria2.tellStatus", @params = new object[] { $"token:{secret}", gid, ProgressKeys } }).ToArray();
            var values = await CallAsync<JsonElement[]>("system.multicall", token, [calls]).ConfigureAwait(false);
            if (values.Length != chunk.Length) throw InvalidResponse();
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var message = value.TryGetProperty("message", out var text) ? text.GetString() : null;
                    if (message?.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) == true) throw AuthenticationError();
                    if (value.TryGetProperty("code", out var code) && code.TryGetInt32(out var number) && number == 1
                        && message?.Contains("is not found", StringComparison.OrdinalIgnoreCase) == true) continue;
                    throw InvalidResponse();
                }
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 1) throw InvalidResponse();
                var transfer = value[0].Deserialize<TransferResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (transfer is null || transfer.Gid != chunk[index]) throw InvalidResponse();
                result.Add(Map(transfer));
            }
        }
        return result;
    }
    public async Task<IReadOnlyList<EngineDownload>> GetActiveAsync(CancellationToken token) =>
        (await CallAsync<TransferResponse[]>("aria2.tellActive", token, [ProgressKeys]).ConfigureAwait(false)).Select(Map).ToArray();

    private static EngineDownload Map(TransferResponse transfer)
    {
        var status = transfer.Status switch
        {
            "active" => DownloadStatus.Downloading, "waiting" => DownloadStatus.Queued, "paused" => DownloadStatus.Paused,
            "complete" => DownloadStatus.Completed, "error" => DownloadStatus.Failed, "removed" => DownloadStatus.Removed,
            _ => throw InvalidResponse()
        };
        long Number(string? text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw InvalidResponse();
        var received = Number(transfer.CompletedLength);
        var size = Number(transfer.TotalLength);
        var speed = Number(transfer.DownloadSpeed);
        long? total = size == 0 && status != DownloadStatus.Completed ? null : size;
        if (total.HasValue && received > total.Value) throw InvalidResponse();
        var failure = status == DownloadStatus.Failed ? DownloadFailureMapper.Map(transfer.ErrorCode, transfer.ErrorMessage) : null;
        return new(transfer.Gid, new(status, received, total, speed), failure?.Reason, HttpStatus: failure?.HttpStatus);
    }

    public async Task<IReadOnlyList<string>> GetWaitingAsync(CancellationToken token, int maxCount = int.MaxValue)
    {
        // Page engine inventory, but expose only owned IDs in the manager.
        var result = new List<string>();
        for (var offset = 0; ; offset += 256)
        {
            var page = await CallAsync<WaitingResponse[]>("aria2.tellWaiting", token, [offset, 256, new[] { "gid" }]).ConfigureAwait(false);
            result.AddRange(page.Select(row => row.Gid));
            if (result.Count > maxCount) throw InvalidResponse();
            if (page.Length < 256) return result;
        }
    }
    public async Task<IReadOnlyList<string>> GetInventoryAsync(CancellationToken token, int maxCount = int.MaxValue)
    {
        var active = await CallAsync<WaitingResponse[]>("aria2.tellActive", token, [new[] { "gid" }]).ConfigureAwait(false);
        var result = active.Select(row => row.Gid).Concat(await GetWaitingAsync(token, maxCount).ConfigureAwait(false)).ToList();
        if (result.Count > maxCount) throw InvalidResponse();
        for (var offset = 0; ; offset += 256)
        {
            var page = await CallAsync<WaitingResponse[]>("aria2.tellStopped", token, [offset, 256, new[] { "gid" }]).ConfigureAwait(false);
            result.AddRange(page.Select(row => row.Gid));
            if (result.Count > maxCount) throw InvalidResponse();
            if (page.Length < 256) return result.Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    public async Task<int> GetConcurrencyAsync(CancellationToken token)
    {
        var options = await CallAsync<Dictionary<string, string>>("aria2.getGlobalOption", token).ConfigureAwait(false);
        return options.TryGetValue("max-concurrent-downloads", out var text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0 ? limit : throw InvalidResponse();
    }
    public async Task SetConcurrencyAsync(int limit, CancellationToken token)
    {
        if (limit is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = await CallAsync<string>("aria2.changeGlobalOption", token,
            [new Dictionary<string, string> { ["max-concurrent-downloads"] = limit.ToString(CultureInfo.InvariantCulture) }]).ConfigureAwait(false);
        if (result != "OK") throw InvalidResponse();
    }

    public async Task<(ByteRateLimit Download, ByteRateLimit Upload)> GetBandwidthAsync(CancellationToken token)
    {
        var options = await CallAsync<Dictionary<string, string>>("aria2.getGlobalOption", token).ConfigureAwait(false);
        static ByteRateLimit Parse(Dictionary<string, string> values, string key)
        {
            if (!values.TryGetValue(key, out var value) || !long.TryParse(value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var bytes) || bytes < 0) throw InvalidResponse();
            return bytes == 0 ? ByteRateLimit.Unlimited : new(bytes);
        }
        return (Parse(options, "max-overall-download-limit"), Parse(options, "max-overall-upload-limit"));
    }

    public async Task SetBandwidthAsync(BandwidthProfile profile, CancellationToken token)
    {
        profile.Validate();
        static string Format(ByteRateLimit cap) => SpeedLimitPresetPolicy.ToAriaOptionValue(cap.BytesPerSecond);
        var result = await CallAsync<string>("aria2.changeGlobalOption", token,
            [new Dictionary<string, string> {
                ["max-overall-download-limit"] = Format(profile.Download),
                ["max-overall-upload-limit"] = Format(profile.Upload)
            }]).ConfigureAwait(false);
        if (result != "OK") throw InvalidResponse();
    }

    public async Task<ByteRateLimit> GetJobBandwidthAsync(string gid, CancellationToken token)
    {
        var options = await CallAsync<Dictionary<string, string>>("aria2.getOption", token, [gid]).ConfigureAwait(false);
        if (!options.TryGetValue("max-download-limit", out var text) ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes < 0)
            throw InvalidResponse();
        return bytes == 0 ? ByteRateLimit.Unlimited : new(bytes);
    }

    public async Task SetJobBandwidthAsync(string gid, ByteRateLimit cap, CancellationToken token)
    {
        cap.Validate();
        var result = await CallAsync<string>("aria2.changeOption", token,
            [gid, new Dictionary<string, string> {
                ["max-download-limit"] = (cap.BytesPerSecond ?? 0).ToString(CultureInfo.InvariantCulture)
            }]).ConfigureAwait(false);
        if (result != "OK") throw InvalidResponse();
    }
    public async Task ExecuteAsync(string gid, DownloadAction action, int? position, CancellationToken token)
    {
        if (action == DownloadAction.Move)
        {
            if (position is null or < 0) throw new ArgumentOutOfRangeException(nameof(position));
            var actual = await CallAsync<int>("aria2.changePosition", token, [gid, position.Value, "POS_SET"]).ConfigureAwait(false);
            if (actual < 0) throw InvalidResponse();
            return;
        }
        var method = action switch
        {
            DownloadAction.Pause => "aria2.pause", DownloadAction.Resume => "aria2.unpause",
            DownloadAction.Remove => "aria2.remove", _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (await CallAsync<string>(method, token, [gid]).ConfigureAwait(false) != gid) throw InvalidResponse();
    }

    private async Task<T> CallAsync<T>(string method, CancellationToken cancellationToken, object[]? parameters = null)
    {
        if (!endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("The local engine RPC endpoint must use HTTP loopback.");
        var id = Interlocked.Increment(ref _requestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = new RpcRequest("2.0", id, method, method == "system.multicall" ? parameters ?? []
            : new object[] { $"token:{secret}" }.Concat(parameters ?? []).ToArray());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            // aria2's HTTP parser needs Content-Length; JsonContent can send a chunked request.
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(endpoint, content, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw AuthenticationError();
            // aria2 reports RPC errors (including invalid tokens) in an HTTP 400 JSON envelope.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
                throw new EngineException(EngineFailure.RpcUnavailable, "aria2 did not accept the local connection.");
            var payload = await response.Content.ReadFromJsonAsync<RpcResponse<T>>(timeout.Token).ConfigureAwait(false);
            if (payload is null || payload.JsonRpc != "2.0" || payload.Id != id) throw InvalidResponse();
            if (payload.Error is not null)
            {
                // Never include the remote error message: it can contain credentials or a sensitive URL.
                if (payload.Error.Message?.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) == true)
                    throw AuthenticationError();
                if (method == "aria2.tellStatus" && payload.Error.Code == 1
                    && payload.Error.Message?.Contains("is not found", StringComparison.OrdinalIgnoreCase) == true)
                    throw new EngineException(EngineFailure.TransferNotFound, "The transfer is not in this engine session.");
                if (method is "aria2.addUri" or "aria2.pause" or "aria2.unpause" or "aria2.remove" or "aria2.changePosition" or "aria2.changeGlobalOption" or "aria2.changeOption")
                    throw new EngineException(EngineFailure.RequestRejected, "The engine rejected the command.");
                throw new EngineException(EngineFailure.RpcUnavailable, "aria2 rejected a local engine request.");
            }
            if (payload.Result is null) throw InvalidResponse();
            if (!response.IsSuccessStatusCode)
                throw new EngineException(EngineFailure.RpcUnavailable, "aria2 rejected a local engine request.");
            return payload.Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new EngineException(EngineFailure.RpcUnavailable, "The local engine connection timed out."); }
        catch (HttpRequestException)
        { throw new EngineException(EngineFailure.RpcUnavailable, "The local engine is not responding."); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static EngineException AuthenticationError() => new(EngineFailure.Authentication,
        "The local engine rejected authentication. Restart it to establish a new connection.");
    private static EngineException InvalidResponse() => new(EngineFailure.InvalidResponse,
        "The engine returned an unexpected response. Check the bundled aria2 version.");

    private sealed record RpcRequest([property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Parameters);
    private sealed record RpcResponse<T>([property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("result")] T? Result,
        [property: JsonPropertyName("error")] RpcError? Error);
    private sealed record RpcError(int Code, string? Message);
    private sealed record VersionResponse(string Version, string[] EnabledFeatures);
    private sealed record SessionResponse(string SessionId);
    private sealed record WaitingResponse(string Gid);
    private sealed record TransferResponse(string Gid, string Status, string TotalLength, string CompletedLength,
        string DownloadSpeed, string? ErrorCode, string? ErrorMessage);
}
