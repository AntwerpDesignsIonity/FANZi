using System;
using System.Collections.Generic;
using System.Linq;

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
    public int PollingIntervalSeconds { get; set; } = 1;
    public int ReducedPollingIntervalSeconds { get; set; } = 10;
    public string? LastExportPath { get; set; }

    /// <summary>User-selected OpenRGB.exe ("Locate OpenRGB.exe…"); null = auto-detect.</summary>
    public string? OpenRgbPath { get; set; }

    /// <summary>4.1: OpenRGB SDK endpoint host (null = this PC, 127.0.0.1). Another PC's address drives its OpenRGB.</summary>
    public string? OpenRgbHost { get; set; }

    /// <summary>4.1: OpenRGB SDK endpoint port (null = 6742).</summary>
    public int? OpenRgbPort { get; set; }

    // Modular tab visibility — let the user disable sections they don't need
    public bool ShowTaskManagerTab { get; set; } = true;
    public bool ShowNetworkManagerTab { get; set; } = true;
    public bool ShowPowerMonitorTab { get; set; } = true;
    public bool ShowSystemCleanerTab { get; set; } = true;
    /// <summary>v4.1.0 Tools tab (disk optimiser, drive health, repair, power &amp; gaming).</summary>
    public bool ShowToolsTab { get; set; } = true;

    // Overlay
    public bool OverlayTransparent { get; set; } = false;
    public double OverlayOpacity { get; set; } = 0.90;
    public bool OverlayTopmost { get; set; } = false;

    /// <summary>CPU temperature sparkline under the overlay's CPU gauge. Missing in older files → default on.</summary>
    public bool OverlayShowCpuHistory { get; set; } = true;

    // v4 overlay studio — every row of the mini overlay can be switched on/off. Missing in older files → defaults.
    public bool OverlayShowCpu { get; set; } = true;
    public bool OverlayShowGpu { get; set; } = true;
    public bool OverlayShowFan { get; set; } = true;
    public bool OverlayShowCpuLoad { get; set; } = true;
    public bool OverlayShowGpuLoad { get; set; } = true;
    public bool OverlayShowRam { get; set; } = true;
    public bool OverlayShowNetwork { get; set; } = true;
    public bool OverlayShowHealth { get; set; } = true;
    public bool OverlayShowClock { get; set; }

    /// <summary>Overlay zoom, 0.8–1.6 (1 = 300 px wide).</summary>
    public double OverlayScale { get; set; } = 1.0;

    /// <summary>Accent preset name (see OverlayAccents); "RGB Sync" follows the live RGB colour.</summary>
    public string OverlayAccent { get; set; } = "Ionity Blue";

    // v4 AEDi lighting (RGB tab). Thermal Guardian defaults on: a critical temperature turns the lights into an alert.
    public bool RgbSmartAmbient { get; set; }
    public bool RgbThermalGuardian { get; set; } = true;
    public bool RgbNightDimming { get; set; }

    /// <summary>Last overlay position in screen pixels; null = default (top-right of the primary screen).</summary>
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }

    /// <summary>In-app toast notifications (temperature, anomaly, OpenRGB, profile alerts); added in v4, default on.</summary>
    public bool ToastNotificationsEnabled { get; set; } = true;

    /// <summary>"Reduce motion": no gauge sweeps, number counts or looping loader animations; added in v5, default off.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Overlay layout: compact = header + gauges only, expanded = gauges + bars + sparkline; added in v5, default expanded.</summary>
    public bool OverlayCompact { get; set; }

    /// <summary>
    /// Running temperature line around the main window frame (blue cold, green stable, red hot); added in 4.1,
    /// default on. Older settings files lack the field, so they pick up the default without a migration step.
    /// </summary>
    public bool ThermalFrameEnabled { get; set; } = true;

    /// <summary>Settings schema version written by this build.</summary>
    public const int CurrentVersion = 6;

    /// <summary>
    /// Applies one-time migrations for settings created by older builds.
    /// v1 → v2: old installs had OverlayTransparent=true (buggy AcrylicBlur on Win11).
    /// v2 → v3: adds OverlayTopmost / OverlayX / OverlayY — the new fields keep their defaults
    /// and every existing user value is left untouched.
    /// v3 → v4: adds ToastNotificationsEnabled — switched on for upgraded installs (the field did not
    /// exist before, so no user choice is overwritten); everything else is left untouched.
    /// v4 → v5: adds ReduceMotion (off) and OverlayCompact (off = expanded, the only layout before v5);
    /// both keep their defaults, so upgraded installs look and move exactly as before.
    /// </summary>
    public void Migrate()
    {
        if (SettingsVersion < 2)
        {
            OverlayTransparent = false;
            OverlayOpacity = 0.90;
        }

        if (SettingsVersion < 4)
            ToastNotificationsEnabled = true;

        // v5 → v6 (FANZi 4.0.1): real-time dashboard. Installs still on the old 3 s default move to 1 s;
        // a value the user picked themselves (anything but 3) is kept.
        if (SettingsVersion < 6 && PollingIntervalSeconds == 3)
            PollingIntervalSeconds = 1;

        SettingsVersion = Math.Max(SettingsVersion, CurrentVersion);
    }

    /// <summary>
    /// Repairs values a hand-edited or damaged settings file can contain: polling intervals are
    /// clamped (a 0 or negative interval would spin the refresh loop) and null profiles are dropped.
    /// Call after <see cref="Migrate"/>.
    /// </summary>
    public void Normalize()
    {
        PollingIntervalSeconds = Math.Clamp(PollingIntervalSeconds, 1, 60);
        ReducedPollingIntervalSeconds = Math.Clamp(ReducedPollingIntervalSeconds, 2, 120);
        Profiles = (Profiles ?? new List<FanProfile>()).Where(profile => profile is not null).ToList();
    }
}
