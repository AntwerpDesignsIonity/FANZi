using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Rgb;
using System;
using System.ComponentModel;
using System.Linq;
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

    // ── 4.5 segments ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _zoneEnabled = true;
    /// <summary>Equal blocks the zone is split into (1–8, never more than its LEDs).</summary>
    [ObservableProperty] private int    _segments = 1;
    [ObservableProperty] private int    _patternIndex;
    [ObservableProperty] private bool   _reverse;
    /// <summary>Zone brightness trim, 10–100 % of the global brightness.</summary>
    [ObservableProperty] private double _zoneBrightness = 100;
    /// <summary>Zone speed trim, 0.25–3× the global speed.</summary>
    [ObservableProperty] private double _zoneSpeed = 1.0;
    [ObservableProperty] private string _secondaryHex = "#00C8FF";
    /// <summary>True while this row differs from what is on the hardware (Apply mode only).</summary>
    [ObservableProperty] private bool   _hasPending;

    public IReadOnlyList<string> Patterns { get; } = new[] { "Solid", "Gradient", "Alternate", "Chase", "Mirror" };
    public int MaxSegments => Math.Max(1, Math.Min(ZoneSegmentRenderer.MaxSegments, LedCount));
    public bool CanSegment => LedCount > 1;
    public string SegmentLabel => Segments == 1 ? "1 segment" : $"{Segments} segments";
    public string ZoneBrightnessLabel => $"{ZoneBrightness:F0} %";
    public string ZoneSpeedLabel => $"{ZoneSpeed:F2}×";

    /// <summary>Stable settings key: device name + index + zone name.</summary>
    public string Key => $"{DeviceName}|{DeviceIndex}|{ZoneName}";

    /// <summary>Set by the RGB view model: true = edits go to the hardware at once.</summary>
    public Func<bool>? IsLive { get; set; }
    /// <summary>Raised after any edit (the parent persists and refreshes its pending state).</summary>
    public Action<ZoneEffectViewModel>? Edited { get; set; }

    /// <summary>What the frame loop renders — the live values, or the last applied ones in Apply mode.</summary>
    private sealed record Snapshot(RgbEffectType Effect, RgbReactiveSource Source, RgbColor Primary, RgbColor Secondary, ZoneRender Render);
    private volatile Snapshot _applied = new(RgbEffectType.Static, RgbReactiveSource.CpuTemperature, RgbColor.Blue, RgbColor.Cyan, ZoneRender.Default);
    private bool _loading;

    private Snapshot Live() => new(SelectedEffect, ReactiveSource, PrimaryColor, SecondaryColor,
        new ZoneRender(ZoneEnabled, ZoneSegmentRenderer.ClampSegments(Segments, LedCount),
            (ZonePattern)Math.Clamp(PatternIndex, 0, 4), Reverse,
            Math.Clamp(ZoneBrightness, 0, 100) / 100.0, Math.Clamp(ZoneSpeed, 0.25, 3.0)));

    /// <summary>Render settings currently on the hardware.</summary>
    public ZoneRender AppliedRender => _applied.Render;
    public RgbColor AppliedSecondary => _applied.Secondary;

    /// <summary>Sends this row's edits to the hardware (Apply).</summary>
    public void Commit() { _applied = Live(); HasPending = false; }

    /// <summary>Puts the controls back to what is on the hardware (Revert).</summary>
    public void Revert()
    {
        var a = _applied;
        _loading = true;
        try
        {
            SelectedEffect = a.Effect; ReactiveSource = a.Source; PrimaryColor = a.Primary; SecondaryColor = a.Secondary;
            ZoneEnabled = a.Render.Enabled; Segments = a.Render.Segments; PatternIndex = (int)a.Render.Pattern; Reverse = a.Render.Reverse;
            ZoneBrightness = a.Render.Brightness * 100; ZoneSpeed = a.Render.Speed;
        }
        finally { _loading = false; }
        HasPending = false;
    }

    /// <summary>Loads persisted settings (no pending state, applied at once).</summary>
    public void Load(RgbZoneSettings s)
    {
        _loading = true;
        try
        {
            ZoneEnabled = s.Enabled;
            if (Enum.TryParse<RgbEffectType>(s.Effect, out var fx) && Effects.Contains(fx)) SelectedEffect = fx;
            if (Enum.TryParse<RgbReactiveSource>(s.Source, out var src)) ReactiveSource = src;
            if (RgbColor.TryParseHex(s.PrimaryHex ?? "", out var p)) PrimaryColor = p;
            if (RgbColor.TryParseHex(s.SecondaryHex ?? "", out var q)) SecondaryColor = q;
            Segments = ZoneSegmentRenderer.ClampSegments(s.Segments, LedCount);
            PatternIndex = Math.Max(0, Array.IndexOf(new[] { "Solid", "Gradient", "Alternate", "Chase", "Mirror" }, s.Pattern ?? "Solid"));
            Reverse = string.Equals(s.Direction, "Reverse", StringComparison.OrdinalIgnoreCase);
            ZoneBrightness = Math.Clamp(s.Brightness, 0, 100);
            ZoneSpeed = Math.Clamp(s.Speed, 0.25, 3.0);
        }
        finally { _loading = false; }
        Commit();
    }

    public RgbZoneSettings ToSettings() => new()
    {
        Enabled = ZoneEnabled, Effect = SelectedEffect.ToString(), Source = ReactiveSource.ToString(),
        PrimaryHex = PrimaryColor.ToHex(), SecondaryHex = SecondaryColor.ToHex(),
        Segments = Segments, Pattern = Patterns[Math.Clamp(PatternIndex, 0, 4)], Direction = Reverse ? "Reverse" : "Forward",
        Brightness = ZoneBrightness, Speed = ZoneSpeed,
    };

    /// <summary>True when the row is exactly the built-in default (nothing worth persisting).</summary>
    public bool IsDefault => ZoneEnabled && Segments == 1 && PatternIndex == 0 && !Reverse && ZoneBrightness >= 100 && Math.Abs(ZoneSpeed - 1) < 0.001
        && SelectedEffect == RgbEffectType.Static && ReactiveSource == RgbReactiveSource.CpuTemperature
        && PrimaryColor == RgbColor.Blue && SecondaryColor == RgbColor.Cyan;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Segments): base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(SegmentLabel))); break;
            case nameof(ZoneBrightness): base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ZoneBrightnessLabel))); break;
            case nameof(ZoneSpeed): base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ZoneSpeedLabel))); break;
        }
        if (_loading) return;
        if (e.PropertyName is nameof(SelectedEffect) or nameof(ReactiveSource) or nameof(PrimaryColor) or nameof(SecondaryColor)
            or nameof(ZoneEnabled) or nameof(Segments) or nameof(PatternIndex) or nameof(Reverse) or nameof(ZoneBrightness) or nameof(ZoneSpeed))
        {
            if (IsLive?.Invoke() ?? true) Commit();
            else HasPending = Live() != _applied;
            Edited?.Invoke(this);
        }
    }

    partial void OnSecondaryHexChanged(string value)
    {
        if (_syncingHex) return;
        if (RgbColor.TryParseHex(value, out var c)) { _syncingHex = true; SecondaryColor = c; _syncingHex = false; }
    }

    partial void OnSecondaryColorChanged(RgbColor value)
    {
        if (_syncingHex) return;
        _syncingHex = true; SecondaryHex = value.ToHex(); _syncingHex = false;
    }

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
        var applied = _applied;
        (double? c, double? g, double? l) = applied.Source switch
        {
            RgbReactiveSource.CpuTemperature => (cpuTempC, (double?)null, (double?)null),
            RgbReactiveSource.GpuTemperature => ((double?)null, gpuTempC, (double?)null),
            RgbReactiveSource.CpuLoad        => ((double?)null, (double?)null, cpuLoadPct),
            RgbReactiveSource.PumpSpeed      => ((double?)null, (double?)null, pumpLoadPct),
            _                                => ((double?)null, (double?)null, (double?)null),
        };

        if (!applied.Render.Enabled) return RgbColor.Black;

        return RgbEffectsEngine.Tick(
            elapsedSeconds:  elapsedSeconds,
            effect:          applied.Effect,
            primary:         applied.Primary,
            secondary:       applied.Secondary,
            speedMultiplier: speedMultiplier * applied.Render.Speed,
            brightness:      brightness * applied.Render.Brightness,
            cpuTempC:        c,
            gpuTempC:        g,
            cpuLoadPct:      l,
            deviceIndex:     ZoneIndex,
            deviceCount:     Math.Max(1, zoneCountForWave));
    }
}
