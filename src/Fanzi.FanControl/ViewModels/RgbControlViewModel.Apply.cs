using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// 4.5: Apply / Revert, Simple mode, the Auto master switch, scene cross-fade, and per-zone segment settings
/// (persistence + the per-LED frame).
/// </summary>
public partial class RgbControlViewModel
{
    // ── Live apply vs Apply button ───────────────────────────────────────────

    /// <summary>On: every change goes to the lights at once. Off: changes wait for Apply (Revert drops them).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApplyBar))]
    [NotifyPropertyChangedFor(nameof(ApplyModeText))]
    private bool _rgbLiveApply = true;

    /// <summary>True while the controls differ from what is on the hardware.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyStatusText))]
    private bool _hasPendingRgbChanges;

    public bool ShowApplyBar => !RgbLiveApply;
    public string ApplyModeText => RgbLiveApply ? "Changes apply instantly" : "Changes wait for Apply";
    public string ApplyStatusText => HasPendingRgbChanges
        ? "Unapplied changes — the lights still show the previous look."
        : "Lights match the controls.";

    private sealed record UserLook(RgbEffectType Effect, RgbColor Primary, RgbColor Secondary, double Speed, double Brightness);
    private volatile UserLook? _appliedLook;

    private UserLook LiveLook() => new(SelectedEffect, PrimaryColor, SecondaryColor, SpeedMultiplier, Brightness);

    /// <summary>The user's look the frame loop renders: live values, or the last applied ones in Apply mode.</summary>
    private UserLook CurrentLook() => RgbLiveApply ? LiveLook() : _appliedLook ?? LiveLook();

    private IRelayCommand? _applyRgbCommand;
    public IRelayCommand ApplyRgbCommand => _applyRgbCommand ??= new RelayCommand(ApplyRgbNow);

    private IRelayCommand? _revertRgbCommand;
    public IRelayCommand RevertRgbCommand => _revertRgbCommand ??= new RelayCommand(RevertRgb);

    private void ApplyRgbNow()
    {
        _appliedLook = LiveLook();
        foreach (var z in ZoneControls) z.Commit();
        HasPendingRgbChanges = false;
        PersistZonesSoon();
        ServerStatus = "Applied — the lights now match the controls.";
    }

    private void RevertRgb()
    {
        if (_appliedLook is { } look)
        {
            SelectedEffect = look.Effect;
            PrimaryColor = look.Primary;
            SecondaryColor = look.Secondary;
            SpeedMultiplier = look.Speed;
            Brightness = look.Brightness;
            SyncPrimarySliders();
            SyncSecondarySliders();
        }
        foreach (var z in ZoneControls) z.Revert();
        HasPendingRgbChanges = false;
        ServerStatus = "Reverted to what the lights are showing.";
    }

    partial void OnRgbLiveApplyChanged(bool value)
    {
        // Either way the lights keep showing exactly what they show now.
        _appliedLook = LiveLook();
        if (value) foreach (var z in ZoneControls) z.Commit();
        HasPendingRgbChanges = false;
        if (!_applyingLookSettings) _persistLook?.Invoke();
    }

    private void RefreshPending()
    {
        if (RgbLiveApply) { _appliedLook = LiveLook(); HasPendingRgbChanges = false; return; }
        HasPendingRgbChanges = (_appliedLook is { } a && a != LiveLook()) || ZoneControls.Any(z => z.HasPending);
    }

    // ── Simple mode + Auto ───────────────────────────────────────────────────

    /// <summary>Simple: Auto switch, themes, colour, brightness. Advanced: every section.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedRgb))]
    [NotifyPropertyChangedFor(nameof(SimpleModeText))]
    private bool _rgbSimpleMode;

    public bool ShowAdvancedRgb => !RgbSimpleMode;
    public string SimpleModeText => RgbSimpleMode ? "Simple view" : "Advanced view";

    /// <summary>
    /// One switch for "just handle it": Smart Ambient (scene follows idle / work / game / heat), Thermal Guardian and
    /// Night dimming on together. Turning it off leaves Thermal Guardian on.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoRgbText))]
    private bool _autoRgb;

    public string AutoRgbText => AutoRgb
        ? "Auto: AEDi picks the scene from what the PC is doing, dims at night and flashes on overheating."
        : "Manual: the lights show the colour, effect and theme you pick.";

    partial void OnAutoRgbChanged(bool value)
    {
        if (_applyingLookSettings || _syncingAuto) return;
        _syncingAuto = true;
        try
        {
            SmartAmbientEnabled = value;
            NightDimmingEnabled = value;
            if (value) ThermalGuardianEnabled = true;
        }
        finally { _syncingAuto = false; }
        _persistLook?.Invoke();
    }

    partial void OnRgbSimpleModeChanged(bool value) { if (!_applyingLookSettings) _persistLook?.Invoke(); }
    partial void OnZoneModeEnabledChanged(bool value) { if (!_applyingLookSettings) _persistLook?.Invoke(); }

    private bool _syncingAuto;
    private bool _applyingLookSettings;
    private Action? _persistLook;
    private Action<Dictionary<string, RgbZoneSettings>>? _persistZones;
    private Dictionary<string, RgbZoneSettings> _savedZones = new(StringComparer.Ordinal);
    private CancellationTokenSource? _zonePersistDelay;
    private bool _lookHooked;

    /// <summary>Loads the 4.5 look settings and the save callbacks (called by MainWindowViewModel).</summary>
    public void ApplyLookSettings(bool liveApply, bool simpleMode, bool auto, bool zoneMode,
        IReadOnlyDictionary<string, RgbZoneSettings>? zones,
        Action<bool, bool, bool, bool> persist, Action<Dictionary<string, RgbZoneSettings>> persistZones)
    {
        _applyingLookSettings = true;
        try
        {
            RgbLiveApply = liveApply;
            RgbSimpleMode = simpleMode;
            ZoneModeEnabled = zoneMode;
            // Auto is only "on" when its parts still are (the user may have switched Smart Ambient off since).
            AutoRgb = auto && SmartAmbientEnabled;
        }
        finally { _applyingLookSettings = false; }

        _savedZones = zones is null
            ? new Dictionary<string, RgbZoneSettings>(StringComparer.Ordinal)
            : new Dictionary<string, RgbZoneSettings>(zones, StringComparer.Ordinal);
        _persistLook = () => persist(RgbLiveApply, RgbSimpleMode, AutoRgb, ZoneModeEnabled);
        _persistZones = persistZones;
        _appliedLook = LiveLook();
        foreach (var z in ZoneControls) AttachZone(z);

        if (!_lookHooked)
        {
            _lookHooked = true;
            PropertyChanged += OnLookPropertyChanged;
        }
    }

    private void OnLookPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SelectedEffect) or nameof(PrimaryColor) or nameof(SecondaryColor) or nameof(SpeedMultiplier) or nameof(Brightness):
                RefreshPending();
                break;
            case nameof(SmartAmbientEnabled) when !_syncingAuto && !_applyingLookSettings:
                // A manual pick pauses Smart Ambient — Auto then reads "off" too.
                if (!SmartAmbientEnabled && AutoRgb)
                {
                    _syncingAuto = true;
                    try { AutoRgb = false; } finally { _syncingAuto = false; }
                    _persistLook?.Invoke();
                }
                break;
        }
    }

    // ── Zones: persistence ───────────────────────────────────────────────────

    /// <summary>Wires a zone row: live/apply mode, saved settings, change notifications.</summary>
    private void AttachZone(ZoneEffectViewModel zone)
    {
        zone.IsLive = () => RgbLiveApply;
        zone.Edited = OnZoneEdited;
        if (_savedZones.TryGetValue(zone.Key, out var saved)) zone.Load(saved);
    }

    private void OnZoneEdited(ZoneEffectViewModel zone)
    {
        RefreshPending();
        if (RgbLiveApply) PersistZonesSoon();
    }

    /// <summary>Saves zone settings 0.8 s after the last edit (sliders fire many changes).</summary>
    private void PersistZonesSoon()
    {
        if (_persistZones is null) return;
        _zonePersistDelay?.Cancel();
        var cts = _zonePersistDelay = new CancellationTokenSource();
        _ = Task.Delay(800, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(PersistZonesNow);
        }, TaskScheduler.Default);
    }

    internal void PersistZonesNow()
    {
        if (_persistZones is null) return;
        foreach (var z in ZoneControls)
        {
            if (z.IsDefault) _savedZones.Remove(z.Key);
            else _savedZones[z.Key] = z.ToSettings();
        }
        _persistZones(new Dictionary<string, RgbZoneSettings>(_savedZones, StringComparer.Ordinal));
    }

    private IRelayCommand? _resetZonesCommand;
    /// <summary>Every zone back to one solid segment at full brightness (colours and effects stay).</summary>
    public IRelayCommand ResetZoneSegmentsCommand => _resetZonesCommand ??= new RelayCommand(() =>
    {
        foreach (var z in ZoneControls)
        {
            z.ZoneEnabled = true; z.Segments = 1; z.PatternIndex = 0; z.Reverse = false; z.ZoneBrightness = 100; z.ZoneSpeed = 1.0;
        }
    });

    private IRelayCommand<ZoneEffectViewModel>? _copyZoneToAllCommand;
    /// <summary>Copies one row's look (effect, colours, segments, pattern…) to every other zone.</summary>
    public IRelayCommand<ZoneEffectViewModel> CopyZoneToAllCommand => _copyZoneToAllCommand ??= new RelayCommand<ZoneEffectViewModel>(source =>
    {
        if (source is null) return;
        var s = source.ToSettings();
        foreach (var z in ZoneControls)
        {
            if (ReferenceEquals(z, source)) continue;
            z.SelectedEffect = source.SelectedEffect; z.ReactiveSource = source.ReactiveSource;
            z.PrimaryColor = source.PrimaryColor; z.SecondaryColor = source.SecondaryColor;
            z.ZoneEnabled = s.Enabled; z.Segments = ZoneSegmentRenderer.ClampSegments(s.Segments, z.LedCount);
            z.PatternIndex = source.PatternIndex; z.Reverse = source.Reverse; z.ZoneBrightness = s.Brightness; z.ZoneSpeed = s.Speed;
        }
        ServerStatus = $"Copied “{source.DisplayLabel}” to {Math.Max(0, ZoneControls.Count - 1)} other zone(s).";
    });

    // ── Zones: per-LED frame ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the per-LED colours of one device from its zones (ordered by zone index — OpenRGB lays LEDs out zone
    /// after zone). Returns null when every zone is plain, so the caller can use the cheaper per-zone update. Pure.
    /// </summary>
    internal static RgbColor[]? BuildDeviceLeds(
        IReadOnlyList<(int LedCount, RgbColor A, RgbColor B, ZoneRender Render)> zonesInOrder, double elapsedSeconds)
    {
        bool plain = true;
        int total = 0;
        foreach (var z in zonesInOrder) { plain &= z.Render.IsPlain; total += Math.Max(0, z.LedCount); }
        if (plain || total == 0) return null;

        var leds = new RgbColor[total];
        int offset = 0;
        foreach (var z in zonesInOrder)
        {
            int n = Math.Max(0, z.LedCount);
            ZoneSegmentRenderer.Render(leds.AsSpan(offset, n), z.A, z.B, z.Render, elapsedSeconds);
            offset += n;
        }
        return leds;
    }

    // ── Scene cross-fade ─────────────────────────────────────────────────────

    /// <summary>Seconds an AEDi scene change takes to fade in (alerts cut in at once).</summary>
    internal const double SceneFadeSeconds = 1.5;

    private (RgbEffectType Effect, RgbColor Primary, RgbColor Secondary, double Speed, double Brightness)? _fadeFrom;
    private double _fadeStart;

    /// <summary>Remembers the look being left so the next frames blend from it.</summary>
    private void BeginSceneFade((RgbEffectType, RgbColor, RgbColor, double, double) from, bool immediate)
    {
        if (immediate) { _fadeFrom = null; return; }
        _fadeFrom = from;
        _fadeStart = _stopwatch.Elapsed.TotalSeconds;
    }

    /// <summary>Colour for a device this frame, blended from the previous scene while a fade is running.</summary>
    private RgbColor TickLook(double elapsed, int deviceIndex, int deviceCount, double? cpuTemp, double? gpuTemp, double? cpuLoad)
    {
        var frame = FrameParameters();
        RgbColor now = RgbEffectsEngine.Tick(elapsed, frame.Effect, frame.Primary, frame.Secondary, frame.Speed, frame.Brightness,
            cpuTemp, gpuTemp, cpuLoad, deviceIndex, deviceCount);

        if (_fadeFrom is not { } from) return now;
        double t = (elapsed - _fadeStart) / SceneFadeSeconds;
        if (t >= 1.0 || t < 0) { _fadeFrom = null; return now; }

        RgbColor before = RgbEffectsEngine.Tick(elapsed, from.Effect, from.Primary, from.Secondary, from.Speed, from.Brightness,
            cpuTemp, gpuTemp, cpuLoad, deviceIndex, deviceCount);
        return RgbColor.Lerp(before, now, SmoothStep(t));
    }

    internal static double SmoothStep(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
}
