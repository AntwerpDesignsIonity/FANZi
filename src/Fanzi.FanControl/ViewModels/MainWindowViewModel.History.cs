using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// Live thermal history: rolling CPU/GPU temperature, load and cooler RPM series (up to 15 min / 900
/// samples each), recorded on the UI thread once per poll and drawn by <c>ThermalHistoryChart</c>.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Window lengths offered by the dashboard selector (minutes).</summary>
    internal static readonly int[] HistoryWindowChoices = { 1, 5, 15 };

    public ThermalHistory History { get; } = new();

    /// <summary>Bumped after every recorded poll so charts re-render (the histories mutate in place).</summary>
    [ObservableProperty] private int _historyRevision;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryWindow), nameof(HistoryTitle),
        nameof(IsHistoryWindow1Min), nameof(IsHistoryWindow5Min), nameof(IsHistoryWindow15Min))]
    private int _historyWindowMinutes = 5;

    /// <summary>Show the tiny CPU temperature sparkline under the overlay's CPU gauge (persisted).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OverlaySparklineVisible))] private bool _overlayShowCpuHistory = true;

    /// <summary>The sparkline belongs to the expanded overlay: shown when enabled and the overlay is not compact.</summary>
    public bool OverlaySparklineVisible => OverlayShowCpuHistory && !OverlayCompact;

    public TimeSpan HistoryWindow => TimeSpan.FromMinutes(HistoryWindowMinutes);

    public string HistoryTitle => $"Thermal history — last {HistoryWindowMinutes} min";

    // Selector state for the 1 / 5 / 15 min segment buttons (unchecking is a no-op; the group checks another).
    public bool IsHistoryWindow1Min { get => HistoryWindowMinutes == 1; set { if (value) HistoryWindowMinutes = 1; } }
    public bool IsHistoryWindow5Min { get => HistoryWindowMinutes == 5; set { if (value) HistoryWindowMinutes = 5; } }
    public bool IsHistoryWindow15Min { get => HistoryWindowMinutes == 15; set { if (value) HistoryWindowMinutes = 15; } }

    partial void OnHistoryWindowMinutesChanged(int value)
    {
        if (Array.IndexOf(HistoryWindowChoices, value) < 0)
            HistoryWindowMinutes = 5;
    }

    partial void OnOverlayShowCpuHistoryChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.OverlayShowCpuHistory = value;
        _ = SaveSettingsAsync();
    }

    /// <summary>Appends one poll to every series and notifies the charts.</summary>
    private void RecordHistory(DateTime timeUtc, double? cpuTempC, double? gpuTempC, double? cpuLoadPct,
        double? gpuLoadPct, double? coolerRpm)
    {
        History.Record(timeUtc, cpuTempC, gpuTempC, cpuLoadPct, gpuLoadPct, coolerRpm);
        NotifyHistoryChanged();
    }

    /// <summary>Re-renders the history charts after <see cref="History"/> was changed directly (render harness).</summary>
    internal void NotifyHistoryChanged() => HistoryRevision++;
}
