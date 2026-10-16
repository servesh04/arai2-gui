using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.Infrastructure.Downloads;

public sealed record HttpRepresentation(int Version, string EffectiveResource, string StrongETag, long Length, string Encoding, bool Resumable = true);
public sealed record HttpTransferCompletion(int Version, long Length, string Sha256);
public sealed record ContinuityResponse(long Offset, long Length, long Total, string StrongETag);
public sealed record ContinuityResourceLimits
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromDays(7);
    public long MaximumBytes { get; init; } = 8L * 1024 * 1024 * 1024 * 1024;
    internal void Validate()
    {
        if (IdleTimeout <= TimeSpan.Zero || HeaderTimeout <= TimeSpan.Zero || MaximumDuration <= TimeSpan.Zero
            || IdleTimeout > TimeSpan.FromMinutes(10) || HeaderTimeout > TimeSpan.FromMinutes(10)
            || MaximumDuration > TimeSpan.FromDays(30) || MaximumBytes <= 0) throw new ArgumentOutOfRangeException();
    }
}

// Implementations must enforce destination policy on the actual connection and each redirect.
// Public composition pins DNS-approved connections and validates TLS at each bounded redirect hop.
public interface IContinuityOriginTransport
{
    Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token);
    // Range-capable routes must opt in explicitly; the v1 transport must never
    // silently turn a bounded interval into an open-ended resume.
    Task<HttpResponseMessage> OpenRangeAsync(long start, long end, string? ifMatch, CancellationToken token) =>
        throw new NotSupportedException("Bounded origin ranges are unavailable.");
}

public sealed class HttpRepresentationStore
{
    private readonly string _path;
    private readonly string _completionPath;
    private readonly string _freshPath;
    private readonly bool _forceFresh;
    public HttpRepresentationStore(string privateRuntimeDirectory, Guid attemptId, Guid? completionAttempt = null, bool forceFresh = false)
    {
        if (!Path.IsPathFullyQualified(privateRuntimeDirectory) || attemptId == Guid.Empty) throw new ArgumentException();
        var directory = Path.Combine(Path.GetFullPath(privateRuntimeDirectory), "representations");
        UserRuntimeDirectory.EnsurePrivate(directory);
        _path = Path.Combine(directory, attemptId.ToString("N") + ".json");
        _completionPath = Path.Combine(directory, (completionAttempt ?? attemptId).ToString("N") + ".completion.json");
        _freshPath = Path.Combine(directory, (completionAttempt ?? attemptId).ToString("N") + ".fresh.json");
        _forceFresh = forceFresh;
    }
    public async Task<HttpRepresentation?> LoadAsync(CancellationToken token)
    {
        var path = _forceFresh || File.Exists(_freshPath) ? _freshPath : _path;
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 65536) throw new IOException("Invalid representation record.");
        var record = JsonSerializer.Deserialize<HttpRepresentation>(await File.ReadAllTextAsync(path, token));
        if (!IsValid(record)) throw new IOException("Invalid representation record.");
        return record;
    }
    internal static bool IsValid(HttpRepresentation? record) => record is { Version: 1 or 2 or 3, Encoding: "identity", EffectiveResource: not null, StrongETag: not null }
        && record.EffectiveResource.Length <= 16384 && record.StrongETag.Length <= 512
        && Uri.TryCreate(record.EffectiveResource, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && (record.Resumable ? record.Length > 0 && EntityTagHeaderValue.TryParse(record.StrongETag, out var etag) && !etag.IsWeak && etag.Tag != "*"
            : record.Version == 2 && record.Length >= -1 && record.StrongETag == "");
    public async Task<HttpTransferCompletion?> LoadCompletionAsync(CancellationToken token)
    {
        if (!File.Exists(_completionPath)) return null;
        if (new FileInfo(_completionPath).Length > 1024 || (File.GetAttributes(_completionPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid completion record.");
        var value = JsonSerializer.Deserialize<HttpTransferCompletion>(await File.ReadAllTextAsync(_completionPath, token));
        if (value is not { Version: 1, Length: >= 0, Sha256.Length: 64 } || !value.Sha256.All(Uri.IsHexDigit)) throw new IOException("Invalid completion record.");
        return value;
    }
    internal async Task SaveCompletionAsync(HttpTransferCompletion value, CancellationToken token)
    {
        var temp = _completionPath + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(file, value, cancellationToken: token); await file.FlushAsync(token); file.Flush(true); }
            File.Move(temp, _completionPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task SaveBeforeBodyAsync(HttpRepresentation record, CancellationToken token)
    {
        if (!IsValid(record)) throw new ArgumentException("A strong representation is required.");
        var existing = await LoadAsync(token);
        if (existing is not null) { if (existing != record) throw new IOException("Representation conflict."); return; }
        var path = _forceFresh || !record.Resumable ? _freshPath : _path;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(file, record, cancellationToken: token); await file.FlushAsync(token); file.Flush(true); }
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { if (await LoadAsync(token) != record) throw; }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// Disabled unless the caller explicitly opts into the experimental transport slice.
// It owns validation/body forwarding, not endpoint authentication or file publication.
public sealed class HttpContinuityRelay
{
    private readonly HttpRepresentationStore _store;
    private readonly IContinuityOriginTransport _transport;
    private readonly bool _requiresRetainedRepresentation;
    private readonly ContinuityResourceLimits _limits;
    private readonly SharedRelayRateLimiter? _upstreamLimit;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly SemaphoreSlim _rangeAdmission = new(4);
    private readonly object _activeRangeGate = new();
    private readonly List<(long Start, long End)> _activeRanges = [];
    internal Task<HttpRepresentation?> GetSequentialRepresentationAsync(CancellationToken token) => _store.LoadAsync(token);
    // The argument validates initial source shape only. Transport owns source/redirect binding;
    // persisted representation checks below bind the eventual effective resource.
    public HttpContinuityRelay(string exactEffectiveResource, HttpRepresentationStore store, IContinuityOriginTransport transport,
        bool requiresRetainedRepresentation, bool enableExperimental = false, ContinuityResourceLimits? limits = null)
        : this(exactEffectiveResource, store, transport, requiresRetainedRepresentation, enableExperimental, limits, null) { }

    internal HttpContinuityRelay(string exactEffectiveResource, HttpRepresentationStore store, IContinuityOriginTransport transport,
        bool requiresRetainedRepresentation, bool enableExperimental, ContinuityResourceLimits? limits, SharedRelayRateLimiter? upstreamLimit)
    {
        if (!enableExperimental) throw new InvalidOperationException("Continuity relay is not enabled.");
        if (exactEffectiveResource.Length > 16384 || !Uri.TryCreate(exactEffectiveResource, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException();
        _store = store; _transport = transport; _requiresRetainedRepresentation = requiresRetainedRepresentation;
        _limits = limits ?? new(); _limits.Validate();
        _upstreamLimit = upstreamLimit;
    }
    public async Task<bool> RelayAsync(long offset, Func<ContinuityResponse, CancellationToken, Task> sendHeaders,
        Stream bodyDestination, CancellationToken token)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(token);
        admission.CancelAfter(_limits.HeaderTimeout);
        await _gate.WaitAsync(admission.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_limits.MaximumDuration);
        try
        {
            var prior = await _store.LoadAsync(deadline.Token);
            if (prior?.Version == 3) return false; // An M3 checkpoint cannot be downgraded to the v1 reader.
            if (prior is { Resumable: false }) return false; // A consumed one-shot body never authorizes another response.
            if ((_requiresRetainedRepresentation || offset > 0) && prior is null) return false;
            if (prior is not null && offset >= prior.Length) return false;
            using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            headersDeadline.CancelAfter(_limits.HeaderTimeout);
            using var response = await _transport.OpenAsync(offset, prior?.StrongETag, headersDeadline.Token);
            var content = response.Content.Headers;
            var etag = response.Headers.ETag;
            var resource = response.RequestMessage?.RequestUri?.OriginalString;
            var encoding = content.ContentEncoding.Count == 0 || content.ContentEncoding.SequenceEqual(["identity"]);
            if (response.StatusCode != (offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                || !encoding || !ValidResource(resource)) return false;
            if (prior is not null && (etag is not { IsWeak: false } || etag.Tag == "*")) return false;
            if (prior is not null && (prior.EffectiveResource != resource || prior.StrongETag != etag!.Tag)) return false;
            if (offset > 0)
            {
                if (content.ContentRange is not { HasRange: true, HasLength: true, Unit: "bytes" } range
                    || range.From != offset || range.To != prior!.Length - 1 || range.Length != prior.Length
                    || content.ContentLength != prior.Length - offset) return false;
            }
            else if (content.ContentRange is not null || (prior is not null && prior.Length != content.ContentLength)) return false;
            if (content.ContentLength > _limits.MaximumBytes || prior?.Length > _limits.MaximumBytes) return false;
            var resumable = etag is { IsWeak: false } && etag.Tag != "*" && content.ContentLength is > 0;
            var representation = prior ?? (resumable ? new HttpRepresentation(1, resource!, etag!.Tag, content.ContentLength!.Value, "identity")
                : new HttpRepresentation(2, resource!, "", content.ContentLength ?? -1, "identity", false));
            await _store.SaveBeforeBodyAsync(representation, deadline.Token);
            headersDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            idle.CancelAfter(_limits.IdleTimeout);
            if (offset == 0 && content.ContentLength == 0)
                await _store.SaveCompletionAsync(new(1, 0, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()))), deadline.Token);
            await sendHeaders(new(offset, content.ContentLength ?? -1, representation.Length, representation.StrongETag), idle.Token);
            idle.CancelAfter(_limits.IdleTimeout);
            await using var body = await response.Content.ReadAsStreamAsync(idle.Token);
            // Bound body to the validated representation; never forward extra bytes.
            var remaining = content.ContentLength ?? long.MaxValue;
            long transferred = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[32768];
            while (remaining > 0)
            {
                idle.CancelAfter(_limits.IdleTimeout);
                var maximum = (int)Math.Min(buffer.Length, remaining);
                var reserved = _upstreamLimit is null ? maximum : await _upstreamLimit.ReserveAsync(maximum, idle.Token);
                int read;
                try { read = await body.ReadAsync(buffer.AsMemory(0, reserved), idle.Token); }
                catch { _upstreamLimit?.Refund(reserved); throw; }
                _upstreamLimit?.Refund(reserved - read);
                if (read == 0) { if (content.ContentLength is null) break; throw new IOException("Origin body ended early."); }
                hash.AppendData(buffer, 0, read); transferred += read;
                if (transferred > _limits.MaximumBytes) throw new IOException("Origin body exceeds resource limit.");
                // Persist before the final bytes make a known-length response observable as complete.
                if (offset == 0 && content.ContentLength is not null && remaining == read)
                    await _store.SaveCompletionAsync(new(1, transferred, Convert.ToHexString(hash.GetCurrentHash())), deadline.Token);
                idle.CancelAfter(_limits.IdleTimeout);
                await bodyDestination.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                idle.CancelAfter(_limits.IdleTimeout);
                await bodyDestination.FlushAsync(idle.Token); remaining -= read;
            }
            // Unknown-length completion is exposed only after this receipt and handler return.
            if (offset == 0 && content.ContentLength is null) await _store.SaveCompletionAsync(new(1, transferred, Convert.ToHexString(hash.GetHashAndReset())), deadline.Token);
            return true;
        }
        finally { _gate.Release(); }
    }
    // Used only by the opt-in v2 guarded route. Buffering each bounded interval
    // ensures a truncated body contributes no bytes to aria2.
    internal async Task<bool> RelayBoundedAsync(long start, long end,
        Func<ContinuityResponse, CancellationToken, Task> sendHeaders, Stream destination, CancellationToken token)
    {
        if (start < 0 || end < start || end - start >= 8L * 1024 * 1024) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_limits.MaximumDuration);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        admission.CancelAfter(_limits.HeaderTimeout);
        await _rangeAdmission.WaitAsync(admission.Token).ConfigureAwait(false);
        var reservedRange = false;
        try
        {
            lock (_activeRangeGate)
            {
                if (_activeRanges.Any(range => start <= range.End && range.Start <= end)) return false;
                _activeRanges.Add((start, end));
                reservedRange = true;
            }
            var prior = await _store.LoadAsync(admission.Token).ConfigureAwait(false);
            if (prior is { Version: not 3 } || prior is null && _requiresRetainedRepresentation
                || prior is not null && end >= prior.Length) return false;
            // Origin headers may race. Only the short durable representation commit
            // is serialized; all bodies and independent cancellations remain concurrent.
            using var response = await _transport.OpenRangeAsync(start, end, prior?.StrongETag, admission.Token).ConfigureAwait(false);
            var content = response.Content.Headers;
            var etag = response.Headers.ETag;
            var resource = response.RequestMessage?.RequestUri?.OriginalString;
            var range = content.ContentRange;
            var expectedLength = end - start + 1;
            if (response.StatusCode != HttpStatusCode.PartialContent || !ValidResource(resource)
                || etag is not { IsWeak: false } || etag.Tag == "*"
                || content.ContentEncoding.Count != 0 && !content.ContentEncoding.SequenceEqual(["identity"])
                || content.ContentType?.MediaType?.Equals("multipart/byteranges", StringComparison.OrdinalIgnoreCase) == true
                || range is not { HasRange: true, HasLength: true, Unit: "bytes" }
                || range.From != start || range.To != end || range.Length is not > 0
                || end >= range.Length || range.Length > _limits.MaximumBytes
                || content.ContentLength != expectedLength) return false;
            HttpRepresentation representation;
            await _gate.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                var committed = await _store.LoadAsync(admission.Token).ConfigureAwait(false);
                if (committed is { Version: not 3 } || committed is not null &&
                    (committed.EffectiveResource != resource || committed.StrongETag != etag.Tag || committed.Length != range.Length)) return false;
                representation = committed ?? new HttpRepresentation(3, resource!, etag.Tag, range.Length!.Value, "identity");
                if (committed is null) await _store.SaveBeforeBodyAsync(representation, admission.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            await using (var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            using (var buffered = new MemoryStream((int)(end - start + 1)))
            {
                var remaining = end - start + 1;
                var buffer = new byte[32768];
                while (remaining > 0)
                {
                    idle.CancelAfter(_limits.IdleTimeout);
                    var maximum = (int)Math.Min(buffer.Length, remaining);
                    var reserved = _upstreamLimit is null ? maximum : await _upstreamLimit.ReserveAsync(maximum, idle.Token).ConfigureAwait(false);
                    int read;
                    try { read = await body.ReadAsync(buffer.AsMemory(0, reserved), idle.Token).ConfigureAwait(false); }
                    catch { _upstreamLimit?.Refund(reserved); throw; }
                    _upstreamLimit?.Refund(reserved - read);
                    if (read == 0) return false;
                    await buffered.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                    remaining -= read;
                }
                idle.CancelAfter(_limits.IdleTimeout);
                if (await body.ReadAsync(buffer.AsMemory(0, 1), idle.Token).ConfigureAwait(false) != 0) return false;
                await sendHeaders(new(start, buffered.Length, representation.Length, representation.StrongETag), deadline.Token).ConfigureAwait(false);
                buffered.Position = 0;
                await buffered.CopyToAsync(destination, deadline.Token).ConfigureAwait(false);
                await destination.FlushAsync(deadline.Token).ConfigureAwait(false);
                return true;
            }
        }
        finally
        {
            if (reservedRange)
                lock (_activeRangeGate) _activeRanges.Remove((start, end));
            _rangeAdmission.Release();
        }
    }
    // aria2 1.37.0 begins a split download with an unbounded GET at zero.
    // Admit that response as a v3 representation initializer after validating
    // its headers; independent bounded requests can proceed while it streams.
    internal async Task<bool> RelayParallelInitialAsync(Func<ContinuityResponse, CancellationToken, Task> sendHeaders,
        Stream destination, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_limits.MaximumDuration);
        using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        headersDeadline.CancelAfter(_limits.HeaderTimeout);
        await _rangeAdmission.WaitAsync(headersDeadline.Token).ConfigureAwait(false);
        try
        {
            var prior = await _store.LoadAsync(headersDeadline.Token).ConfigureAwait(false);
            if (prior is { Version: not 3 } || prior is null && _requiresRetainedRepresentation) return false;
            using var response = await _transport.OpenAsync(0, prior?.StrongETag, headersDeadline.Token).ConfigureAwait(false);
            var content = response.Content.Headers;
            var etag = response.Headers.ETag;
            var resource = response.RequestMessage?.RequestUri?.OriginalString;
            if (response.StatusCode != HttpStatusCode.OK || !ValidResource(resource)
                || etag is not { IsWeak: false } || etag.Tag == "*"
                || content.ContentEncoding.Count != 0 && !content.ContentEncoding.SequenceEqual(["identity"])
                || content.ContentRange is not null || content.ContentLength is not > 0
                || content.ContentLength > _limits.MaximumBytes) return false;
            HttpRepresentation representation;
            await _gate.WaitAsync(headersDeadline.Token).ConfigureAwait(false);
            try
            {
                var committed = await _store.LoadAsync(headersDeadline.Token).ConfigureAwait(false);
                if (committed is { Version: not 3 } || committed is not null &&
                    (committed.EffectiveResource != resource || committed.StrongETag != etag.Tag || committed.Length != content.ContentLength)) return false;
                representation = committed ?? new HttpRepresentation(3, resource!, etag.Tag, content.ContentLength.Value, "identity");
                if (committed is null) await _store.SaveBeforeBodyAsync(representation, headersDeadline.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            idle.CancelAfter(_limits.IdleTimeout);
            await sendHeaders(new(0, representation.Length, representation.Length, representation.StrongETag), idle.Token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            var remaining = representation.Length;
            var buffer = new byte[32768];
            while (remaining > 0)
            {
                idle.CancelAfter(_limits.IdleTimeout);
                var maximum = (int)Math.Min(buffer.Length, remaining);
                var reserved = _upstreamLimit is null ? maximum : await _upstreamLimit.ReserveAsync(maximum, idle.Token).ConfigureAwait(false);
                int read;
                try { read = await body.ReadAsync(buffer.AsMemory(0, reserved), idle.Token).ConfigureAwait(false); }
                catch { _upstreamLimit?.Refund(reserved); throw; }
                _upstreamLimit?.Refund(reserved - read);
                if (read == 0) throw new IOException("Origin body ended early.");
                await destination.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                await destination.FlushAsync(idle.Token).ConfigureAwait(false);
                remaining -= read;
            }
            idle.CancelAfter(_limits.IdleTimeout);
            if (await body.ReadAsync(buffer.AsMemory(0, 1), idle.Token).ConfigureAwait(false) != 0)
                throw new IOException("Origin body exceeded the representation.");
            return true;
        }
        finally { _rangeAdmission.Release(); }
    }
    private static bool ValidResource(string? value) => value is { Length: <= 16384 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
}
