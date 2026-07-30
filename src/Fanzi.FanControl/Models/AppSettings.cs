using System.Collections.Generic;

namespace Fanzi.FanControl.Models;

public sealed class AppSettings
{
    /// <summary>Bumped when defaults change so old settings files get migrated.</summary>
    public int SettingsVersion { get; set; }

    public List<FanProfile> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool AiAutoFanEnabled { get; set; }
    public bool AiAnomalyDetection { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 3;
    public int ReducedPollingIntervalSeconds { get; set; } = 10;
    public string? LastExportPath { get; set; }

    // Modular tab visibility — let the user disable sections they don't need
    public bool ShowTaskManagerTab { get; set; } = true;
    public bool ShowNetworkManagerTab { get; set; } = true;
    public bool ShowPowerMonitorTab { get; set; } = true;
    public bool ShowSystemCleanerTab { get; set; } = true;

    // Overlay
    public bool OverlayTransparent { get; set; } = false;
    public double OverlayOpacity { get; set; } = 0.90;

    /// <summary>
    /// Applies one-time migrations for settings created before v2.1.
    /// Old installs had OverlayTransparent=true (buggy AcrylicBlur on Win11).
    /// </summary>
    public void Migrate()
    {
        const int CurrentVersion = 2;

        if (SettingsVersion < 2)
        {
            OverlayTransparent = false;
            OverlayOpacity = 0.90;
        }

        SettingsVersion = CurrentVersion;
    }
}
