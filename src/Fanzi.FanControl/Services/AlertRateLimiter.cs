using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.Services;

/// <summary>
/// "Same alert at most once per window" gate. Each alert key passes once, then is held back until the
/// window (default 2 minutes) has elapsed since it last passed.
/// </summary>
internal sealed class AlertRateLimiter
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(2);

    private const int PruneAbove = 64;

    private readonly Dictionary<string, DateTime> _lastPassedUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _utcNow;

    public AlertRateLimiter(TimeSpan? window = null, Func<DateTime>? utcNow = null)
    {
        Window = window is { } w && w > TimeSpan.Zero ? w : DefaultWindow;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public TimeSpan Window { get; }

    /// <summary>True (and the key is recorded) when <paramref name="key"/> has not passed within the window.</summary>
    public bool TryAcquire(string key)
    {
        DateTime now = _utcNow();
        if (_lastPassedUtc.TryGetValue(key, out DateTime last) && now - last < Window && now >= last)
            return false;

        _lastPassedUtc[key] = now;
        if (_lastPassedUtc.Count > PruneAbove)
        {
            foreach (var stale in _lastPassedUtc.Where(kv => now - kv.Value >= Window).Select(kv => kv.Key).ToList())
                _lastPassedUtc.Remove(stale);
        }
        return true;
    }

    /// <summary>Forgets every key (the next alert of each kind passes).</summary>
    public void Reset() => _lastPassedUtc.Clear();
}
