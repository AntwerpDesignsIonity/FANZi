using System;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services;

/// <summary>Where an alert ended up.</summary>
internal enum AlertRoute
{
    /// <summary>The same alert already went out within the rate-limit window.</summary>
    RateLimited,
    /// <summary>Toasts are switched off in Settings: logged only.</summary>
    LogOnly,
    /// <summary>Shown as an in-app toast (window visible).</summary>
    Toast,
    /// <summary>Window hidden in the tray: shown in the tray tooltip.</summary>
    Tray,
}

/// <summary>
/// Pure alert router: rate-limits by key, always logs what passes, then shows a toast while the window is
/// visible or hands the alert to the tray while it is hidden. Toasts off (Settings) ⇒ log only.
/// </summary>
internal sealed class AlertNotifier
{
    private readonly AlertRateLimiter _limiter;
    private readonly Action<string> _log;

    public AlertNotifier(AlertRateLimiter? limiter = null, Action<string>? log = null)
    {
        _limiter = limiter ?? new AlertRateLimiter();
        _log = log ?? (message => AppLog.Info(message));
    }

    public AlertRateLimiter Limiter => _limiter;

    /// <summary>Mirrors the persisted "Toast notifications" setting.</summary>
    public bool ToastsEnabled { get; set; } = true;

    /// <summary>False while the main window is hidden in the tray.</summary>
    public bool WindowVisible { get; set; } = true;

    /// <summary>Raised for alerts that should appear as an in-app toast.</summary>
    public event Action<AppNotification>? ToastRequested;

    /// <summary>Raised for alerts that should appear in the tray (window hidden, or no toast host attached).</summary>
    public event Action<AppNotification>? TrayRequested;

    public AlertRoute Notify(string key, AppNotification notification)
    {
        if (!_limiter.TryAcquire(key))
            return AlertRoute.RateLimited;

        _log($"Alert [{notification.Kind}/{notification.Level}] {notification.Summary}");
        if (!ToastsEnabled)
            return AlertRoute.LogOnly;

        if (WindowVisible && ToastRequested is { } toast)
        {
            toast(notification);
            return AlertRoute.Toast;
        }

        // Avalonia's TrayIcon has no balloon/toast API, so the tray route is the tooltip (plus the log above).
        TrayRequested?.Invoke(notification);
        return AlertRoute.Tray;
    }
}
