using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// Per-zone lighting settings for one addressable region of an OpenRGB device
/// (a RAM stick, a fan ring, a GPU block…). Each zone carries its own effect,
/// colours and reactive hardware source, so e.g. RAM can pulse with CPU temp
/// while the GPU block follows GPU temp and a fan ring tracks the AIO pump.
/// </summary>
public sealed partial class ZoneEffectViewModel : ViewModelBase
{
    public int    DeviceIndex { get; }
    public int    ZoneIndex   { get; }
    public string DeviceName  { get; }
    public string ZoneName    { get; }
    public int    LedCount    { get; }

    public string DisplayLabel => $"{DeviceName} · {ZoneName}";
    public string LedLabel      => LedCount == 1 ? "1 LED" : $"{LedCount} LEDs";

    public static IReadOnlyList<RgbEffectType> AvailableEffects { get; } =
    [
        RgbEffectType.Static,
        RgbEffectType.Pulse,
        RgbEffectType.Rainbow,
        RgbEffectType.ColorWave,
        RgbEffectType.TemperatureReactive,
        RgbEffectType.CpuLoadReactive,
        RgbEffectType.Performance,
        RgbEffectType.Strobe,
        RgbEffectType.DualColorFlash,
    ];

    public static IReadOnlyList<RgbReactiveSource> AvailableSources { get; } =
    [
        RgbReactiveSource.None,
        RgbReactiveSource.CpuTemperature,
        RgbReactiveSource.GpuTemperature,
        RgbReactiveSource.CpuLoad,
        RgbReactiveSource.PumpSpeed,
    ];

    // Instance accessors so compiled XAML bindings (x:DataType) can reach the lists.
    public IReadOnlyList<RgbEffectType>     Effects => AvailableEffects;
    public IReadOnlyList<RgbReactiveSource> Sources => AvailableSources;

    [ObservableProperty] private RgbEffectType     _selectedEffect = RgbEffectType.Static;
    [ObservableProperty] private RgbReactiveSource _reactiveSource = RgbReactiveSource.CpuTemperature;
    [ObservableProperty] private RgbColor          _primaryColor   = RgbColor.Blue;
    [ObservableProperty] private RgbColor          _secondaryColor = RgbColor.Cyan;
    [ObservableProperty] private string            _primaryHex     = "#0064FF";
    [ObservableProperty] private RgbColor          _previewColor   = RgbColor.Blue;

    /// <summary>Last colour the frame loop computed for this zone (used to update the preview).</summary>
    public RgbColor LastComputed { get; set; } = RgbColor.Blue;

    private bool _syncingHex;

    public ZoneEffectViewModel(RgbZoneInfo zone, string deviceName)
    {
        DeviceIndex = zone.DeviceIndex;
        ZoneIndex   = zone.ZoneIndex;
        DeviceName  = deviceName;
        ZoneName    = zone.Name;
        LedCount    = zone.LedCount;
    }

    partial void OnPrimaryHexChanged(string value)
    {
        if (_syncingHex) return;
        if (RgbColor.TryParseHex(value, out var c))
        {
            _syncingHex = true;
            PrimaryColor = c;
            _syncingHex = false;
        }
    }

    partial void OnPrimaryColorChanged(RgbColor value)
    {
        if (_syncingHex) return;
        _syncingHex = true;
        PrimaryHex = value.ToHex();
        _syncingHex = false;
    }

    /// <summary>
    /// Computes this zone's colour for the current frame, routing the chosen
    /// hardware signal into the shared effects engine.
    /// </summary>
    public RgbColor Compute(
        double  elapsedSeconds,
        double  speedMultiplier,
        double  brightness,
        double? cpuTempC,
        double? gpuTempC,
        double? cpuLoadPct,
        double? pumpLoadPct,
        int     zoneCountForWave)
    {
        (double? c, double? g, double? l) = ReactiveSource switch
        {
            RgbReactiveSource.CpuTemperature => (cpuTempC, (double?)null, (double?)null),
            RgbReactiveSource.GpuTemperature => ((double?)null, gpuTempC, (double?)null),
            RgbReactiveSource.CpuLoad        => ((double?)null, (double?)null, cpuLoadPct),
            RgbReactiveSource.PumpSpeed      => ((double?)null, (double?)null, pumpLoadPct),
            _                                => ((double?)null, (double?)null, (double?)null),
        };

        return RgbEffectsEngine.Tick(
            elapsedSeconds:  elapsedSeconds,
            effect:          SelectedEffect,
            primary:         PrimaryColor,
            secondary:       SecondaryColor,
            speedMultiplier: speedMultiplier,
            brightness:      brightness,
            cpuTempC:        c,
            gpuTempC:        g,
            cpuLoadPct:      l,
            deviceIndex:     ZoneIndex,
            deviceCount:     Math.Max(1, zoneCountForWave));
    }
}
