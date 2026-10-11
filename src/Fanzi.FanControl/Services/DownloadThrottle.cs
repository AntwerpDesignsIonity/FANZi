using System;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Allows an action (the automatic OpenRGB download) at most once per interval.
/// The clock is injectable so the window can be tested without waiting.
/// </summary>
internal sealed class DownloadThrottle
{
    private readonly TimeSpan _interval;
    private readonly Func<DateTime> _utcNow;
    private readonly object _sync = new();
    private DateTime? _lastAttemptUtc;

    public DownloadThrottle(TimeSpan interval, Func<DateTime>? utcNow = null)
    {
        _interval = interval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public TimeSpan Interval => _interval;

    /// <summary>True (and records the attempt) when no attempt happened within the interval.</summary>
    public bool TryAcquire()
    {
        lock (_sync)
        {
            DateTime now = _utcNow();
            if (_lastAttemptUtc is DateTime last && now - last < _interval)
                return false;
            _lastAttemptUtc = now;
            return true;
        }
    }

    /// <summary>Records an attempt that bypassed the throttle (e.g. user clicked Retry).</summary>
    public void RecordAttempt()
    {
        lock (_sync) _lastAttemptUtc = _utcNow();
    }

    /// <summary>Time until the next attempt is allowed (zero when allowed now).</summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_sync)
            {
                if (_lastAttemptUtc is not DateTime last) return TimeSpan.Zero;
                TimeSpan left = _interval - (_utcNow() - last);
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }
}
