using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using OverlayAccentModel = Fanzi.FanControl.Models.OverlayAccent;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// v4 Overlay Studio: which rows the mini overlay shows, its size and accent, RAM / network readouts and
/// the corner-snap commands. Every switch is persisted in <see cref="AppSettings"/>.
/// </summary>
public partial class MainWindowViewModel
{
    public const double OverlayScaleMin = 0.8;
    public const double OverlayScaleMax = 1.6;

    [ObservableProperty] private bool _overlayShowCpu = true;
    [ObservableProperty] private bool _overlayShowGpu = true;
    [ObservableProperty] private bool _overlayShowFan = true;
    [ObservableProperty] private bool _overlayShowCpuLoad = true;
    [ObservableProperty] private bool _overlayShowGpuLoad = true;
    [ObservableProperty] private bool _overlayShowRam = true;
    [ObservableProperty] private bool _overlayShowNetwork = true;
    [ObservableProperty] private bool _overlayShowHealth = true;
    [ObservableProperty] private bool _overlayShowClock;
    [ObservableProperty] private double _overlayScale = 1.0;
    [ObservableProperty] private string _overlayAccent = OverlayAccentModel.All[0].Name;

    /// <summary>Accent preset names for the Settings picker.</summary>
    public IReadOnlyList<string> OverlayAccentNames => OverlayAccentModel.Names;

    /// <summary>Physical memory in use, 0–100 (overlay RAM bar, AI insights).</summary>
    [ObservableProperty] private double _ramUsedPercentValue;

    /// <summary>"12.4 / 32 GB".</summary>
    [ObservableProperty] private string _ramUsageText = "—";

    /// <summary>Wall clock for the overlay header (updated with the live-sync heartbeat).</summary>
    [ObservableProperty] private string _overlayClock = DateTime.Now.ToString("HH:mm");

    /// <summary>Overlay Studio / title-bar button caption.</summary>
    public string OverlayToggleText => MiniOverlayVisible ? "Hide overlay" : "Show overlay";

    partial void OnMiniOverlayVisibleChanged(bool value) => OnPropertyChanged(nameof(OverlayToggleText));

    /// <summary>True when the overlay shows at least one gauge (hides the empty gauge row).</summary>
    public bool OverlayGaugesVisible => OverlayShowCpu || OverlayShowGpu;

    /// <summary>Bars are part of the expanded layout only.</summary>
    public bool OverlayFanRowVisible => OverlayShowFan && !OverlayCompact;
    public bool OverlayCpuLoadRowVisible => OverlayShowCpuLoad && !OverlayCompact;
    public bool OverlayGpuLoadRowVisible => OverlayShowGpuLoad && !OverlayCompact;
    public bool OverlayRamRowVisible => OverlayShowRam && !OverlayCompact;
    public bool OverlayNetworkRowVisible => OverlayShowNetwork && !OverlayCompact;

    /// <summary>Raised when a property that changes the overlay's size or look changed (the window re-measures).</summary>
    public event EventHandler? OverlayLayoutChanged;

    /// <summary>Status line under the Overlay Studio card (show / hide / errors).</summary>
    [ObservableProperty] private string _overlayStatus = "";

    public IRelayCommand<string> SnapOverlayCommand { get; private set; } = null!;
    public IRelayCommand ResetOverlayLayoutCommand { get; private set; } = null!;
    public IRelayCommand<string> SetOverlayScaleCommand { get; private set; } = null!;
    public IRelayCommand<string> SetOverlayAccentCommand { get; private set; } = null!;
    public IRelayCommand OpenOverlayStudioCommand { get; private set; } = null!;

    /// <summary>Tab index of Settings (Dashboard … Alerts, Settings).</summary>
    public const int SettingsTabIndex = 9;

    /// <summary>The main window scrolls the Overlay Studio card into view.</summary>
    public event EventHandler? OverlayStudioRequested;

    private IDisposable? _memorySync;

    /// <summary>Called from the constructor: commands and the RAM collector on the live-sync heartbeat.</summary>
    private void InitializeOverlayStudio()
    {
        SnapOverlayCommand = new RelayCommand<string>(corner => OverlaySnapRequested?.Invoke(this, corner ?? "TopRight"));
        ResetOverlayLayoutCommand = new RelayCommand(ResetOverlayLayout);
        SetOverlayScaleCommand = new RelayCommand<string>(text =>
        {
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale))
                OverlayScale = scale;
        });
        SetOverlayAccentCommand = new RelayCommand<string>(name => OverlayAccent = OverlayAccentModel.ByName(name).Name);
        OpenOverlayStudioCommand = new RelayCommand(() =>
        {
            ShowWindowCommand.Execute(null);
            SelectedTabIndex = SettingsTabIndex;
            OverlayStudioRequested?.Invoke(this, EventArgs.Empty);
        });
        _memorySync = LiveSyncHub.Shared.Register("Memory", CollectMemoryAsync);
        ApplyMemory(SystemMemory.Read());
    }

    /// <summary>Corner name ("TopLeft", "TopRight", "BottomLeft", "BottomRight") the overlay should snap to.</summary>
    public event EventHandler<string>? OverlaySnapRequested;

    /// <summary>Reads RAM on the thread pool (the hub runs collectors off the UI thread).</summary>
    private Task<Action?> CollectMemoryAsync(CancellationToken token)
    {
        var reading = SystemMemory.Read();
        return Task.FromResult<Action?>(() => { if (!_disposed) ApplyMemory(reading); });
    }

    private void ApplyMemory(SystemMemory.Reading? reading)
    {
        OverlayClock = DateTime.Now.ToString("HH:mm");
        if (reading is not SystemMemory.Reading r) return;
        RamUsedPercentValue = Math.Round(r.UsedPercent, 1);
        RamUsageText = r.Display;
    }

    private void ResetOverlayLayout()
    {
        OverlayShowCpu = OverlayShowGpu = OverlayShowFan = OverlayShowCpuLoad = true;
        OverlayShowGpuLoad = OverlayShowRam = OverlayShowNetwork = OverlayShowHealth = true;
        OverlayShowClock = false;
        OverlayCompact = false;
        OverlayScale = 1.0;
        OverlayAccent = OverlayAccentModel.All[0].Name;
        OverlayTransparent = false;
        OverlayOpacity = 0.90;
        OverlayStatus = "Overlay reset to the default layout";
    }

    /// <summary>Loads the overlay studio switches from settings (called inside the suppress-save block).</summary>
    private void ApplyOverlayStudioSettings()
    {
        OverlayShowCpu = _appSettings.OverlayShowCpu;
        OverlayShowGpu = _appSettings.OverlayShowGpu;
        OverlayShowFan = _appSettings.OverlayShowFan;
        OverlayShowCpuLoad = _appSettings.OverlayShowCpuLoad;
        OverlayShowGpuLoad = _appSettings.OverlayShowGpuLoad;
        OverlayShowRam = _appSettings.OverlayShowRam;
        OverlayShowNetwork = _appSettings.OverlayShowNetwork;
        OverlayShowHealth = _appSettings.OverlayShowHealth;
        OverlayShowClock = _appSettings.OverlayShowClock;
        OverlayScale = Math.Clamp(_appSettings.OverlayScale <= 0 ? 1.0 : _appSettings.OverlayScale, OverlayScaleMin, OverlayScaleMax);
        OverlayAccent = OverlayAccentModel.ByName(_appSettings.OverlayAccent).Name;
    }

    private void SaveOverlayStudio(Action<AppSettings> write)
    {
        OverlayLayoutChanged?.Invoke(this, EventArgs.Empty);
        if (_suppressProfileSync) return;
        write(_appSettings);
        _ = SaveSettingsAsync();
    }

    private void NotifyOverlayRows()
    {
        OnPropertyChanged(nameof(OverlayGaugesVisible));
        OnPropertyChanged(nameof(OverlayFanRowVisible));
        OnPropertyChanged(nameof(OverlayCpuLoadRowVisible));
        OnPropertyChanged(nameof(OverlayGpuLoadRowVisible));
        OnPropertyChanged(nameof(OverlayRamRowVisible));
        OnPropertyChanged(nameof(OverlayNetworkRowVisible));
    }

    partial void OnOverlayShowCpuChanged(bool value) { NotifyOverlayRows(); SaveOverlayStudio(s => s.OverlayShowCpu = value); }
    partial void OnOverlayShowGpuChanged(bool value) { NotifyOverlayRows(); SaveOverlayStudio(s => s.OverlayShowGpu = value); }
    partial void OnOverlayShowFanChanged(bool value) { NotifyOverlayRows(); SaveOverlayStudio(s => s.OverlayShowFan = value); }
    partial void OnOverlayShowCpuLoadChanged(bool value) { NotifyOverlayRows(); SaveOverlayStudio(s => s.OverlayShowCpuLoad = value); }
    partial void OnOverlayShowGpuLoadChanged(bool value) { NotifyOverlayRows(); SaveOverlayStudio(s => s.OverlayShowGpuLoad = value); }
    partial void OnOverlayShowRamChanged(bool value) { NotifyOverlayRows(); SaveOverlayStudio(s => s.OverlayShowRam = value); }
    partial void OnOverlayShowNetworkChanged(bool value) { NotifyOverlayRows(); SaveOverlayStudio(s => s.OverlayShowNetwork = value); }
    partial void OnOverlayShowHealthChanged(bool value) => SaveOverlayStudio(s => s.OverlayShowHealth = value);
    partial void OnOverlayShowClockChanged(bool value) => SaveOverlayStudio(s => s.OverlayShowClock = value);

    partial void OnOverlayScaleChanged(double value)
    {
        double clamped = Math.Clamp(Math.Round(value, 2), OverlayScaleMin, OverlayScaleMax);
        if (Math.Abs(clamped - value) > 0.0001) { OverlayScale = clamped; return; }
        SaveOverlayStudio(s => s.OverlayScale = clamped);
    }

    partial void OnOverlayAccentChanged(string value)
        => SaveOverlayStudio(s => s.OverlayAccent = OverlayAccentModel.ByName(value).Name);

    /// <summary>Disposes the RAM collector (called from Dispose).</summary>
    private void DisposeOverlayStudio()
    {
        _memorySync?.Dispose();
        _memorySync = null;
    }
}
