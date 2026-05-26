using System.Collections.Generic;

namespace Fanzi.FanControl.Models;

public sealed class AppSettings
{
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
}
