using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// Fans-tab curve editor state: the active profile's <see cref="FanProfile.FanCurve"/> in editor form.
/// Every committed edit (drag release, add, remove, nudge, preset, reset) is written into the profile —
/// which <see cref="SmartFanCurveEngine.ComputeOptimalFanSpeed"/> reads on the next poll — and saved
/// through the normal settings path.
/// </summary>
public partial class MainWindowViewModel
{
    private bool _suppressFanCurveSync;

    /// <summary>Points shown by <c>FanCurveEditor</c> (two-way bound).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FanCurvePresetLabel), nameof(FanCurveDutyLabel),
        nameof(IsFanCurveSilent), nameof(IsFanCurveBalanced), nameof(IsFanCurvePerformance), nameof(IsFanCurveFull))]
    private IReadOnlyList<CurvePoint> _fanCurvePoints = FanCurveMath.Preset(FanCurvePreset.Balanced);

    /// <summary>
    /// True when the active profile stores a curve (≥ 2 points), so the engine follows it. A profile
    /// without one shows the Balanced preset as a preview until the user edits or picks a preset.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FanCurveStatus))]
    private bool _fanCurveInUse;

    private IRelayCommand? _resetFanCurveCommand;

    /// <summary>Back to the default curve (Balanced — the one new profiles start with).</summary>
    public IRelayCommand ResetFanCurveCommand => _resetFanCurveCommand ??= new RelayCommand(() => ApplyFanCurvePreset(FanCurvePreset.Balanced));

    /// <summary>"Balanced", "Silent", … or "Custom".</summary>
    public string FanCurvePresetLabel => FanCurveMath.MatchPreset(FanCurvePoints)?.ToString() ?? "Custom";

    public string FanCurveStatus => !FanCurveInUse
        ? "Preview — drag a point or pick a preset to use this curve"
        : AiAutoFanEnabled
            ? "Active — AI Auto-Fan drives the CPU fan from this curve"
            : "Saved to this profile — turn on AI Auto-Fan to drive the CPU fan from it";

    /// <summary>Live curve result for the current CPU temperature (the engine's curve input).</summary>
    public string FanCurveDutyLabel => HottestTempValue is double t && FanCurveMath.Interpolate(FanCurvePoints, t) is var d && double.IsFinite(d)
        ? $"{t.ToString("F0", CultureInfo.CurrentCulture)}° → {d.ToString("F0", CultureInfo.CurrentCulture)}%"
        : "--";

    // Preset segment buttons: checked when the curve equals that preset; checking one applies it.
    public bool IsFanCurveSilent { get => IsPreset(FanCurvePreset.Silent); set { if (value) ApplyFanCurvePreset(FanCurvePreset.Silent); } }
    public bool IsFanCurveBalanced { get => IsPreset(FanCurvePreset.Balanced); set { if (value) ApplyFanCurvePreset(FanCurvePreset.Balanced); } }
    public bool IsFanCurvePerformance { get => IsPreset(FanCurvePreset.Performance); set { if (value) ApplyFanCurvePreset(FanCurvePreset.Performance); } }
    public bool IsFanCurveFull { get => IsPreset(FanCurvePreset.Full); set { if (value) ApplyFanCurvePreset(FanCurvePreset.Full); } }

    private bool IsPreset(FanCurvePreset preset) => FanCurveMath.MatchPreset(FanCurvePoints) == preset;

    public void ApplyFanCurvePreset(FanCurvePreset preset) => FanCurvePoints = FanCurveMath.Preset(preset);

    /// <summary>Shows the profile's curve in the editor (no write-back, so loading never changes a profile).</summary>
    private void LoadFanCurveFromProfile(FanProfile? profile)
    {
        var normalized = FanCurveMath.Normalize(profile?.FanCurve);
        bool inUse = profile?.FanCurve is { Count: >= FanCurveMath.MinPoints } && normalized.Count >= FanCurveMath.MinPoints;

        _suppressFanCurveSync = true;
        try
        {
            FanCurvePoints = inUse ? normalized : FanCurveMath.Preset(FanCurvePreset.Balanced);
            FanCurveInUse = inUse;
        }
        finally
        {
            _suppressFanCurveSync = false;
        }
    }

    partial void OnFanCurvePointsChanged(IReadOnlyList<CurvePoint> value)
    {
        if (_suppressFanCurveSync || _activeProfileTab is null || value is not { Count: >= FanCurveMath.MinPoints }) return;

        // A fresh list each time: the poll loop reads Profile.FanCurve by reference.
        _activeProfileTab.Profile.FanCurve = FanCurveMath.ToModel(value);
        _activeProfileTab.Profile.UseFanCurve = true;
        FanCurveInUse = true;
        _ = SaveSettingsAsync();
    }

    partial void OnHottestTempValueChanged(double? value) => OnPropertyChanged(nameof(FanCurveDutyLabel));
}
