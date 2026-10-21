using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// 4.3 RAM lighting: SMBus mirror handling, the "Re-sync RAM" recovery action and the host-port override.
/// See <see cref="Services.Rgb.SmbusMirrorResolver"/> for why RAM stopped accepting colours before 4.3.
/// </summary>
public sealed partial class RgbControlViewModel
{
    /// <summary>One line under the device list: which SMBus port drives the sticks and what was skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRamBusStatus))]
    private string _ramBusStatus = "";

    public bool HasRamBusStatus => !string.IsNullOrWhiteSpace(RamBusStatus);

    /// <summary>True when OpenRGB lists the same stick on two SMBus ports — shows the "Use other port" button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwapRamPortButtonText))]
    private bool _hasRamMirrors;

    /// <summary>Forced SMBus port (null = automatic). Persisted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwapRamPortButtonText))]
    private int? _ramSmbusPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResyncRamButtonText))]
    private bool _isResyncingRam;

    public string ResyncRamButtonText => IsResyncingRam ? "Re-syncing…" : "↻ Re-sync RAM";

    public string SwapRamPortButtonText
    {
        get
        {
            var ports = _rgbService.MirroredSmbusPorts;
            if (ports.Count < 2) return "Use other SMBus port";
            int current = RamSmbusPort ?? CurrentDrivenRamPort() ?? ports[^1];
            int next = ports.FirstOrDefault(p => p != current, ports[0]);
            return $"Try SMBus port {next} instead";
        }
    }

    private IAsyncRelayCommand? _resyncRamCommand;
    /// <summary>Re-arms the RAM controllers (mode re-applied, colour re-sent). The fix for "RAM stopped changing colour".</summary>
    public IAsyncRelayCommand ResyncRamCommand => _resyncRamCommand ??= new AsyncRelayCommand(ResyncRamAsync, () => !IsResyncingRam);

    private IAsyncRelayCommand? _swapRamPortCommand;
    /// <summary>Drives the sticks through the other SMBus host port (when the automatic pick was wrong).</summary>
    public IAsyncRelayCommand SwapRamPortCommand => _swapRamPortCommand ??= new AsyncRelayCommand(SwapRamPortAsync);

    private IRelayCommand? _autoRamPortCommand;
    public IRelayCommand AutoRamPortCommand => _autoRamPortCommand ??= new RelayCommand(() => { RamSmbusPort = null; _ = RescanDevicesAsync(); });

    private Action<int?>? _persistRamPort;
    private bool _applyingRamSettings;

    /// <summary>Loads the persisted port override and the save callback (called by MainWindowViewModel).</summary>
    public void ApplyRamSettings(int? smbusPort, Action<int?> persist)
    {
        _applyingRamSettings = true;
        try { RamSmbusPort = smbusPort; }
        finally { _applyingRamSettings = false; }
        _persistRamPort = persist;
        _rgbService.RamSmbusPort = smbusPort;
    }

    partial void OnRamSmbusPortChanged(int? value)
    {
        _rgbService.RamSmbusPort = value;
        if (!_applyingRamSettings) _persistRamPort?.Invoke(value);
    }

    /// <summary>Called after every device refresh (UI thread).</summary>
    private void RefreshRamStatus()
    {
        RamBusStatus = _rgbService.RamBusStatus;
        HasRamMirrors = _rgbService.MirroredSmbusPorts.Count > 1 || Devices.Any(d => d.IsMirror);
        OnPropertyChanged(nameof(SwapRamPortButtonText));
    }

    private int? CurrentDrivenRamPort()
    {
        var driven = Devices.FirstOrDefault(d => d.Category == RgbDeviceCategory.Ram && d.IsControllable);
        return driven is null ? null : Services.Rgb.SmbusMirrorResolver.Parse(driven.Location)?.Port;
    }

    private async Task ResyncRamAsync()
    {
        if (!_rgbService.IsConnected)
        {
            AiLightingStatus = "Re-sync RAM: not connected to OpenRGB or MAXSUN Sync.";
            return;
        }
        IsResyncingRam = true;
        ResyncRamCommand.NotifyCanExecuteChanged();
        try
        {
            int n = await _rgbService.ResyncSmbusDevicesAsync(FrameParameters().Primary, _cts.Token);
            bool viaMaxsun = _rgbService is MaxsunSyncRgbService { OwnsRam: true };
            AiLightingStatus = n > 0 && viaMaxsun
                ? $"Re-synced {n} RAM stick(s) through MAXSUN Sync: Static re-armed on the ENE HAL and the current colour re-sent."
                : n > 0
                ? $"Re-synced {n} RAM stick(s): Direct mode re-applied and the current colour re-sent. If a stick still ignores colours, try the other SMBus port."
                : "Re-sync RAM: no SMBus RAM device is under FANZi control right now — run Rescan first.";
            AppLog.Info("RGB: " + AiLightingStatus);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("RGB: RAM re-sync failed", ex);
            AiLightingStatus = $"Re-sync RAM failed: {ex.Message}";
        }
        finally
        {
            IsResyncingRam = false;
            ResyncRamCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task SwapRamPortAsync()
    {
        var ports = _rgbService.MirroredSmbusPorts;
        if (ports.Count < 2) return;
        int current = RamSmbusPort ?? CurrentDrivenRamPort() ?? ports[^1];
        int next = ports.FirstOrDefault(p => p != current, ports[0]);
        RamSmbusPort = next;
        AiLightingStatus = $"RAM now driven through SMBus port {next} — rescanning…";
        await RescanDevicesAsync();
        await ResyncRamAsync();
    }
}
