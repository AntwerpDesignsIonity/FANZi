using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// Alerts &amp; notifications: temperature over threshold, anomalies, OpenRGB connected/failed and profile
/// saved/loaded. Each alert key passes at most once per 2 minutes (<see cref="AlertRateLimiter"/>). While the
/// window is visible the view shows a toast (<see cref="ToastRequested"/>); while it is hidden in the tray the
/// alert is appended to the tray tooltip. "Toast notifications" off (Settings, persisted) ⇒ log only.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>How long a tray alert stays in the tooltip (cleared earlier when the window is shown).</summary>
    internal static readonly TimeSpan TrayAlertLifetime = TimeSpan.FromMinutes(10);

    private readonly AlertNotifier _alerts = new();
    private string _trayBaseTooltip = TrayTooltipFormatter.Default;
    private AppNotification? _trayAlert;
    private bool _rgbWasConnected;
    private bool _alertsWired;

    /// <summary>Raised on the UI thread for alerts the window should show as a toast.</summary>
    public event EventHandler<AppNotification>? ToastRequested;

    /// <summary>In-app toast notifications on/off (persisted as <see cref="AppSettings.ToastNotificationsEnabled"/>).</summary>
    [ObservableProperty] private bool _toastNotificationsEnabled = true;

    internal AlertNotifier Alerts => _alerts;

    /// <summary>Alert currently shown in the tray tooltip, if any.</summary>
    internal AppNotification? TrayAlert => _trayAlert;

    /// <summary>Hooks the alert sources that live outside the poll (OpenRGB state, tray routing). Called once from the constructor.</summary>
    private void WireAlerts()
    {
        if (_alertsWired) return;
        _alertsWired = true;
        _alerts.ToastRequested += notification => ToastRequested?.Invoke(this, notification);
        _alerts.TrayRequested += ShowTrayAlert;
        _rgbWasConnected = RgbControl.IsConnected;
        RgbControl.PropertyChanged += OnRgbControlPropertyChanged;
    }

    private void UnwireAlerts()
    {
        if (!_alertsWired) return;
        RgbControl.PropertyChanged -= OnRgbControlPropertyChanged;
    }

    partial void OnToastNotificationsEnabledChanged(bool value)
    {
        _alerts.ToastsEnabled = value;
        if (!value) ClearTrayAlert();
        if (_suppressProfileSync) return;
        _appSettings.ToastNotificationsEnabled = value;
        _ = SaveSettingsAsync();
    }

    /// <summary>
    /// Routes one alert. Marshals to the UI thread when needed (returns null in that case, and once disposed).
    /// </summary>
    internal AlertRoute? RaiseAlert(AppAlertKind kind, AppAlertLevel level, string key, string title, string message)
    {
        if (_disposed) return null;
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RaiseAlert(kind, level, key, title, message));
            return null;
        }

        _alerts.ToastsEnabled = ToastNotificationsEnabled;
        // No view attached (e.g. before the window exists) counts as hidden: the tray still gets it.
        // A minimised window can't show a toast either, so it goes to the tray too.
        _alerts.WindowVisible = _isWindowVisible && !_isWindowMinimized && ToastRequested is not null;
        return _alerts.Notify(key, new AppNotification(kind, level, title, message, DateTime.UtcNow));
    }

    /// <summary>Tab a toast click opens: the heatmap for thermal alerts, the RGB tab for OpenRGB.</summary>
    internal void OpenAlertTarget(AppNotification notification)
    {
        int? tab = notification.Kind switch
        {
            AppAlertKind.TemperatureHigh or AppAlertKind.Anomaly => SensorsTabIndex,
            AppAlertKind.OpenRgbConnected or AppAlertKind.OpenRgbFailed => RgbTabIndex,
            _ => null,
        };
        if (tab is int index) SelectedTabIndex = index;
    }

    /// <summary>Index of the Sensors tab (heatmap) in the main TabControl.</summary>
    internal const int SensorsTabIndex = 2;

    // ── Alert sources ──────────────────────────────────────────────────────────

    private void RaiseTemperatureAlert(double celsius, double threshold)
    {
        var level = celsius >= threshold + 5 ? AppAlertLevel.Error : AppAlertLevel.Warning;
        RaiseAlert(AppAlertKind.TemperatureHigh, level, "cpu-temperature", "CPU temperature high",
            $"{celsius:F0}°C — above your {threshold:F0}°C alert threshold. Check the Sensors heatmap for the hottest cores.");
    }

    private void RaiseAnomalyAlert(AnomalyReport report)
    {
        if (report.Anomalies.Count == 0) return;
        string first = report.Anomalies[0];
        int colon = first.IndexOf(':');
        string category = colon > 0 ? first[..colon] : first;
        bool critical = report.Severity == AI.AnomalySeverity.Critical;
        RaiseAlert(AppAlertKind.Anomaly, critical ? AppAlertLevel.Error : AppAlertLevel.Warning,
            "anomaly:" + category, critical ? "Critical anomaly detected" : "Anomaly detected",
            string.Join(" · ", report.Anomalies));
    }

    private void OnRgbControlPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RgbControlViewModel.IsConnected))
        {
            bool connected = RgbControl.IsConnected;
            if (connected && !_rgbWasConnected)
                RaiseAlert(AppAlertKind.OpenRgbConnected, AppAlertLevel.Success, "openrgb:connected", "OpenRGB connected",
                    "RGB lighting is live — devices and effects are on the RGB tab.");
            _rgbWasConnected = connected;
        }
        else if (e.PropertyName == nameof(RgbControlViewModel.LastOpenRgbError)
                 && RgbControl.LastOpenRgbError is { Length: > 0 } error)
        {
            RaiseAlert(AppAlertKind.OpenRgbFailed, AppAlertLevel.Error, "openrgb:failed", "OpenRGB problem", error);
        }
    }

    // ── Tray routing ──────────────────────────────────────────────────────────

    private bool _isWindowMinimized;

    /// <summary>Called by the window when it is minimised / restored (alerts route to the tray while minimised).</summary>
    public void SetWindowMinimized(bool minimized)
    {
        _isWindowMinimized = minimized;
        if (!minimized && _isWindowVisible) ClearTrayAlert();
    }

    /// <summary>The tooltip has room for one alert: a newer one replaces it unless the current one is more severe.</summary>
    private void ShowTrayAlert(AppNotification notification)
    {
        bool currentActive = _trayAlert is { } current && DateTime.UtcNow - current.TimeUtc < TrayAlertLifetime;
        if (!currentActive || notification.Level >= _trayAlert!.Level)
            _trayAlert = notification;
        RefreshTrayTooltip();
    }

    private void ClearTrayAlert()
    {
        if (_trayAlert is null) return;
        _trayAlert = null;
        RefreshTrayTooltip();
    }

    /// <summary>Readings tooltip plus the active tray alert (dropped after <see cref="TrayAlertLifetime"/>).</summary>
    private void RefreshTrayTooltip()
    {
        if (_trayAlert is { } alert && DateTime.UtcNow - alert.TimeUtc >= TrayAlertLifetime)
            _trayAlert = null;
        TrayTooltip = TrayTooltipFormatter.WithAlert(_trayBaseTooltip, _trayAlert?.Summary);
    }
}
