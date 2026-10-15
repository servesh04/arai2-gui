using System.Diagnostics;
using AriaGui.Application.Downloads;

namespace AriaGui.Infrastructure.Downloads;

// One budget is shared by every guarded relay route. Reserve before reading the
// origin, so Kestrel/aria2 loopback buffering cannot pull an unbounded body ahead.
internal sealed class SharedRelayRateLimiter
{
    private readonly object _gate = new();
    private long? _rate;
    private double _tokens;
    private long _stamp = Stopwatch.GetTimestamp();

    public void SetRate(ByteRateLimit cap)
    {
        cap.Validate();
        lock (_gate)
        {
            Refill();
            _rate = cap.BytesPerSecond;
            _tokens = _rate is long rate ? Math.Min(_tokens, Math.Min(rate, 32768)) : 0;
            if (_rate is not null && _tokens < 0) _tokens = 0;
        }
    }

    public async Task<int> ReserveAsync(int maximum, CancellationToken token)
    {
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                Refill();
                if (_rate is not long rate) return maximum;
                var chunk = (int)Math.Min(maximum, Math.Min(16384, Math.Max(256, rate / 4)));
                if (_tokens >= chunk) { _tokens -= chunk; return chunk; }
                wait = TimeSpan.FromSeconds((chunk - _tokens) / rate);
            }
            await Task.Delay(wait, token).ConfigureAwait(false);
        }
    }

    public void Refund(int bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            Refill();
            if (_rate is long rate) _tokens = Math.Min(_tokens + bytes, Math.Min(rate, 32768));
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        if (_rate is long rate)
            _tokens = Math.Min(_tokens + (now - _stamp) * (double)rate / Stopwatch.Frequency, Math.Min(rate, 32768));
        _stamp = now;
    }
}
