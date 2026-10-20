using AriaGui.Application.Engine;

namespace AriaGui.Application.Downloads;

public sealed record BrowserHandoffSubmission(Guid Id, string DisplayName, bool AddedPaused);
public interface IBrowserHandoffSubmitter
{
    Task<BrowserHandoffSubmission> SubmitAsync(BrowserHandoffRequest request, bool startPaused, CancellationToken token);
    Task<bool> UndoAsync(Guid id, CancellationToken token);
}
public interface IBrowserHandoffStatus
{
    bool Connected { get; }
    DateTimeOffset? LastHandshake { get; }
}

public sealed class BrowserHandoffService(IUserPreferencesRepository preferences, EngineStateStore engine,
    IBrowserHandoffSubmitter submitter, TimeProvider clock) : IBrowserHandoffStatus
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _requests = new();
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastHandshake;
    public event EventHandler<BrowserHandoffSubmission>? Added;
    public DateTimeOffset? LastHandshake { get { lock (_gate) return _lastHandshake; } }
    public bool Connected => LastHandshake is { } last && clock.GetUtcNow() - last < TimeSpan.FromMinutes(5);

    public BrowserHandoffResponse Hello()
    {
        lock (_gate) _lastHandshake = clock.GetUtcNow();
        return new(true);
    }

    public async Task<BrowserHandoffResponse> HelloAsync(CancellationToken token)
    {
        try
        {
            var saved = await preferences.LoadAsync(token).ConfigureAwait(false);
            saved.Validate();
            var current = saved.EffectiveBrowser;
            lock (_gate) _lastHandshake = clock.GetUtcNow();
            return new(true, Settings: new(current.Enabled, current.AutomaticTakeoverEnabled, current.AltBypass,
                current.MinimumSizeBytes, current.EffectiveCaptureFileTypes, current.EffectiveExcludedSites));
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, Error: "unavailable"); }
    }

    public async Task<BrowserHandoffResponse> HandleAsync(BrowserHandoffRequest request, CancellationToken token)
    {
        BrowserIntegrationPreferences settings;
        try
        {
            var saved = await preferences.LoadAsync(token).ConfigureAwait(false);
            saved.Validate();
            settings = saved.EffectiveBrowser;
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, Error: "unavailable"); }
        if (engine.Current.Status != EngineStatus.Ready) return new(false, Error: "engine-not-ready");
        if (!settings.Enabled || request.Trigger != "contextMenu" && !settings.AutomaticTakeoverEnabled
            || request.Method is not null and not "GET" || request.Url.Scheme is not ("http" or "https"))
            return new(false, Reason: "not-captured");
        if (request.Trigger != "contextMenu" && KeepInBrowser(request, settings))
            return new(false, Reason: "not-captured");

        var now = clock.GetUtcNow();
        var key = request.Url.AbsoluteUri;
        lock (_gate)
        {
            while (_requests.Count > 0 && now - _requests.Peek() >= TimeSpan.FromSeconds(1)) _requests.Dequeue();
            foreach (var expired in _recent.Where(pair => now - pair.Value >= TimeSpan.FromSeconds(5)).Select(pair => pair.Key).ToArray())
                _recent.Remove(expired);
            if (_requests.Count >= 10) return new(false, Error: "rate-limited");
            _requests.Enqueue(now);
            if (_recent.ContainsKey(key)) return new(false, Error: "duplicate");
            _recent[key] = now;
        }
        try
        {
            var submission = await submitter.SubmitAsync(request, settings.StartPaused, token).ConfigureAwait(false);
            Added?.Invoke(this, submission);
            return new(true, submission.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate) _recent.Remove(key);
            return new(false, Error: "timeout");
        }
        catch
        {
            lock (_gate) _recent.Remove(key);
            return new(false, Error: "unavailable");
        }
    }

    public Task<bool> UndoAsync(Guid id, CancellationToken token = default) => submitter.UndoAsync(id, token);

    private static bool KeepInBrowser(BrowserHandoffRequest request, BrowserIntegrationPreferences settings)
    {
        if (request.SizeBytes is { } size && size < settings.MinimumSizeBytes) return true;
        if (settings.EffectiveExcludedSites.Any(site => MatchesSite(request.Url.Host, site)
            || request.TabSite is { } tab && MatchesSite(tab.Host, site))) return true;
        if (settings.EffectiveCaptureFileTypes.Count == 0) return false;
        var extension = Path.GetExtension(request.Filename ?? request.Url.AbsolutePath);
        return !settings.EffectiveCaptureFileTypes.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesSite(string host, string site) => host.Equals(site, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase);
}
