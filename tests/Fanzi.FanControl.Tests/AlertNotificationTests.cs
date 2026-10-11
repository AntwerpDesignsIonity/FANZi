using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>Rate limiting ("same alert at most once per 2 minutes") and toast / tray / log-only routing.</summary>
public sealed class AlertNotificationTests
{
    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    private static AppNotification Alert(string title = "CPU temperature high") =>
        new(AppAlertKind.TemperatureHigh, AppAlertLevel.Warning, title, "96°C — above your 95°C alert threshold.", DateTime.UtcNow);

    [Fact]
    public void RateLimiter_DefaultWindowIsTwoMinutes()
        => Assert.Equal(TimeSpan.FromMinutes(2), new AlertRateLimiter().Window);

    [Fact]
    public void RateLimiter_SameKey_PassesOncePerTwoMinutes()
    {
        var clock = new Clock();
        var limiter = new AlertRateLimiter(utcNow: () => clock.Now);

        Assert.True(limiter.TryAcquire("cpu-temperature"));
        Assert.False(limiter.TryAcquire("cpu-temperature"));
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.False(limiter.TryAcquire("cpu-temperature"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire("cpu-temperature"));   // exactly 2 min later
        Assert.False(limiter.TryAcquire("CPU-TEMPERATURE"));  // keys are case-insensitive
    }

    [Fact]
    public void RateLimiter_DifferentKeysAreIndependent()
    {
        var clock = new Clock();
        var limiter = new AlertRateLimiter(utcNow: () => clock.Now);

        Assert.True(limiter.TryAcquire("cpu-temperature"));
        Assert.True(limiter.TryAcquire("openrgb:connected"));
        Assert.True(limiter.TryAcquire("profile:saved:Gaming"));
        Assert.False(limiter.TryAcquire("openrgb:connected"));
    }

    [Fact]
    public void RateLimiter_ResetAndClockGoingBack_LetTheAlertThrough()
    {
        var clock = new Clock();
        var limiter = new AlertRateLimiter(utcNow: () => clock.Now);
        Assert.True(limiter.TryAcquire("a"));

        limiter.Reset();
        Assert.True(limiter.TryAcquire("a"));

        clock.Advance(TimeSpan.FromMinutes(-30)); // system clock moved back
        Assert.True(limiter.TryAcquire("a"));
    }

    [Fact]
    public void RateLimiter_ManyKeys_StaysBounded()
    {
        var clock = new Clock();
        var limiter = new AlertRateLimiter(utcNow: () => clock.Now);
        for (int i = 0; i < 500; i++)
        {
            Assert.True(limiter.TryAcquire("k" + i));
            clock.Advance(TimeSpan.FromSeconds(1)); // pruning only drops keys older than the window
        }
        // Still limiting after pruning has run many times.
        Assert.False(limiter.TryAcquire("k499")); // 1 s ago
        Assert.False(limiter.TryAcquire("k400")); // 100 s ago
        Assert.True(limiter.TryAcquire("k0"));    // 500 s ago (pruned or not, it is due again)
    }

    [Fact]
    public void Notifier_VisibleWindow_ShowsAToastOnce()
    {
        var clock = new Clock();
        var log = new List<string>();
        var notifier = new AlertNotifier(new AlertRateLimiter(utcNow: () => clock.Now), log.Add);
        var toasts = new List<AppNotification>();
        var tray = new List<AppNotification>();
        notifier.ToastRequested += toasts.Add;
        notifier.TrayRequested += tray.Add;

        Assert.Equal(AlertRoute.Toast, notifier.Notify("cpu-temperature", Alert()));
        Assert.Equal(AlertRoute.RateLimited, notifier.Notify("cpu-temperature", Alert()));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(AlertRoute.Toast, notifier.Notify("cpu-temperature", Alert()));

        Assert.Equal(2, toasts.Count);
        Assert.Empty(tray);
        Assert.Equal(2, log.Count);
        Assert.Contains("CPU temperature high", log[0]);
    }

    [Fact]
    public void Notifier_HiddenWindow_GoesToTheTray()
    {
        var notifier = new AlertNotifier(new AlertRateLimiter(), _ => { }) { WindowVisible = false };
        var toasts = new List<AppNotification>();
        var tray = new List<AppNotification>();
        notifier.ToastRequested += toasts.Add;
        notifier.TrayRequested += tray.Add;

        Assert.Equal(AlertRoute.Tray, notifier.Notify("anomaly:Fan stall detected", Alert("Critical anomaly detected")));

        Assert.Empty(toasts);
        Assert.Equal("Critical anomaly detected", Assert.Single(tray).Title);
    }

    [Fact]
    public void Notifier_NoToastHost_FallsBackToTheTray()
    {
        var notifier = new AlertNotifier(new AlertRateLimiter(), _ => { });
        AppNotification? tray = null;
        notifier.TrayRequested += n => tray = n;

        Assert.Equal(AlertRoute.Tray, notifier.Notify("openrgb:connected", Alert("OpenRGB connected")));
        Assert.NotNull(tray);
    }

    [Fact]
    public void Notifier_ToastsDisabled_LogsOnly_AndStillRateLimits()
    {
        var log = new List<string>();
        var notifier = new AlertNotifier(new AlertRateLimiter(), log.Add) { ToastsEnabled = false };
        int shown = 0;
        notifier.ToastRequested += _ => shown++;
        notifier.TrayRequested += _ => shown++;

        Assert.Equal(AlertRoute.LogOnly, notifier.Notify("cpu-temperature", Alert()));
        Assert.Equal(AlertRoute.RateLimited, notifier.Notify("cpu-temperature", Alert()));

        Assert.Equal(0, shown);
        Assert.Single(log);
    }
}
