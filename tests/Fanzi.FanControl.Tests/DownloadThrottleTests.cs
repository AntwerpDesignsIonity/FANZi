using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

public sealed class DownloadThrottleTests
{
    private DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private DownloadThrottle Create() => new(RgbControlViewModel.AutoDownloadInterval, () => _now);

    [Fact]
    public void AutoDownloadInterval_IsTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), RgbControlViewModel.AutoDownloadInterval);
    }

    [Fact]
    public void FirstAttempt_IsAllowed()
    {
        var throttle = Create();

        Assert.True(throttle.TryAcquire());
        Assert.Equal(TimeSpan.FromMinutes(10), throttle.Remaining);
    }

    [Fact]
    public void SecondAttemptWithinTenMinutes_IsRejected()
    {
        var throttle = Create();
        Assert.True(throttle.TryAcquire());

        _now = _now.AddSeconds(15);   // watchdog tick
        Assert.False(throttle.TryAcquire());

        _now = _now.AddMinutes(9).AddSeconds(44); // 9:59 after the first attempt
        Assert.False(throttle.TryAcquire());
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.Remaining);
    }

    [Fact]
    public void AttemptAfterTenMinutes_IsAllowedAndRestartsWindow()
    {
        var throttle = Create();
        Assert.True(throttle.TryAcquire());

        _now = _now.AddMinutes(10);
        Assert.True(throttle.TryAcquire());

        _now = _now.AddMinutes(5);
        Assert.False(throttle.TryAcquire());
    }

    [Fact]
    public void RejectedAttempts_DoNotExtendTheWindow()
    {
        var throttle = Create();
        Assert.True(throttle.TryAcquire());

        for (int i = 0; i < 39; i++)
        {
            _now = _now.AddSeconds(15);
            Assert.False(throttle.TryAcquire());
        }

        _now = _now.AddSeconds(15); // exactly 10 minutes after the first attempt
        Assert.True(throttle.TryAcquire());
    }

    [Fact]
    public void RecordAttempt_StartsWindowWithoutAcquire()
    {
        var throttle = Create();
        throttle.RecordAttempt(); // e.g. user clicked Retry

        _now = _now.AddMinutes(3);
        Assert.False(throttle.TryAcquire());
        Assert.Equal(TimeSpan.FromMinutes(7), throttle.Remaining);
    }
}
