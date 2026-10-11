using System;

namespace Fanzi.FanControl.Models;

/// <summary>What raised an in-app alert.</summary>
public enum AppAlertKind { TemperatureHigh, Anomaly, OpenRgbConnected, OpenRgbFailed, ProfileSaved, ProfileLoaded }

/// <summary>Toast styling / urgency (maps onto Avalonia's NotificationType).</summary>
public enum AppAlertLevel { Information, Success, Warning, Error }

/// <summary>One alert shown as a toast (window visible) or in the tray tooltip (window hidden).</summary>
public sealed record AppNotification(AppAlertKind Kind, AppAlertLevel Level, string Title, string Message, DateTime TimeUtc)
{
    /// <summary>Single-line form for the log and the tray tooltip.</summary>
    public string Summary => $"{Title}: {Message}";
}
