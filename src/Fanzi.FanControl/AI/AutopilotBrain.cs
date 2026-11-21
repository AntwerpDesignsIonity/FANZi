using Fanzi.FanControl.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

/// <summary>What the user wants from the PC's cooling — the only choice Autopilot asks for.</summary>
public enum AutopilotGoal
{
    /// <summary>Noise first: low fan speeds, ramps only when it has to.</summary>
    Quiet,
    /// <summary>Quiet at the desk, firm under load (default).</summary>
    Balanced,
    /// <summary>Temperature first: fans ramp early.</summary>
    Cool,
}

/// <summary>What Autopilot thinks the PC is doing right now.</summary>
public enum AutopilotScene
{
    Idle,
    Everyday,
    Gaming,
    HeavyWork,
    QuietHours,
    /// <summary>A component is close to its limit — maximum airflow, whatever the goal.</summary>
    Protect,
}

/// <summary>One poll's worth of readings for <see cref="AutopilotBrain"/>.</summary>
public readonly record struct AutopilotInputs(
    double? CpuTempC,
    double? GpuTempC,
    double CpuLoadPercent,
    double GpuLoadPercent,
    bool GameMode,
    string GameName,
    DateTime LocalNow);

/// <summary>What Autopilot decided for this poll.</summary>
/// <param name="Scene">The detected activity.</param>
/// <param name="CurveName">Name of the built-in curve in use ("Silent", "Gaming", …).</param>
/// <param name="CpuCurve">Curve the CPU fan follows (fed to <see cref="SmartFanCurveEngine"/>).</param>
/// <param name="SystemFanPercent">Smoothed duty for the system / case fans.</param>
/// <param name="Headline">Short status, e.g. "Gaming — Cyberpunk2077".</param>
/// <param name="Reason">One sentence on why.</param>
public sealed record AutopilotDecision(
    AutopilotScene Scene,
    string CurveName,
    List<FanCurvePoint> CpuCurve,
    double SystemFanPercent,
    string Headline,
    string Reason);

/// <summary>
/// 4.6 AEDi Autopilot — one switch instead of profiles, curves and per-fan sliders. Each poll it works out what the
/// PC is doing (idle, everyday, gaming, heavy work, quiet hours, or a component near its limit), picks the matching
/// built-in fan curve for the chosen <see cref="AutopilotGoal"/>, and derives one duty for the system fans from the
/// combined CPU + GPU heat. Pure decision logic: no hardware access, no clock of its own (time comes in through
/// <see cref="AutopilotInputs.LocalNow"/>), so every rule is unit-tested.
///
/// Stability rules: a scene must hold for <see cref="SceneDwell"/> before another replaces it (Protect is entered at
/// once and left only after cooling <see cref="ProtectReleaseDeltaC"/> below its threshold), loads are averaged, and
/// the system-fan duty is slew-limited — so fans do not hunt when the load flickers.
/// </summary>
public sealed class AutopilotBrain
{
    /// <summary>Minimum time in a scene before switching to another (Protect excepted).</summary>
    public static readonly TimeSpan SceneDwell = TimeSpan.FromSeconds(20);

    public const double ProtectCpuTempC = 90;
    public const double ProtectGpuTempC = 88;
    /// <summary>Protect ends once both parts are this far below their threshold.</summary>
    public const double ProtectReleaseDeltaC = 8;

    public const double HeavyWorkCpuLoadPercent = 70;
    public const double IdleLoadPercent = 15;
    public const double IdleCpuTempC = 55;

    /// <summary>Quiet hours: 22:00 up to 07:00 local time.</summary>
    public const int QuietHoursStart = 22;
    public const int QuietHoursEnd = 7;

    /// <summary>System fans never go below this — many case fans stall or tick under ~20 %.</summary>
    public const double MinSystemFanPercent = 25;

    /// <summary>A GPU die runs hotter than a CPU for the same case heat; this much is taken off before comparing.</summary>
    public const double GpuTempOffsetC = 8;

    /// <summary>Case fans carry heat out, they do not sit on the die: they run at this share of the curve result.</summary>
    public const double SystemFanShare = 0.85;

    private const double LoadEmaAlpha = 0.15;       // ≈ 12-sample window at 1 poll / s
    private const double RampUpPerSecond = 5.0;     // % per second
    private const double RampDownPerSecond = 2.0;
    private const double HysteresisPercent = 3.0;

    private AutopilotScene _scene = AutopilotScene.Everyday;
    private AutopilotScene _candidate = AutopilotScene.Everyday;
    private DateTime _candidateSince = DateTime.MinValue;
    private DateTime _lastTick = DateTime.MinValue;
    private double _cpuLoadEma = double.NaN;
    private double _gpuLoadEma = double.NaN;
    private double _systemPercent = double.NaN;
    private bool _started;

    public AutopilotGoal Goal { get; set; } = AutopilotGoal.Balanced;

    /// <summary>Use the Silent curve between 22:00 and 07:00 unless the PC is gaming or working hard.</summary>
    public bool QuietHoursEnabled { get; set; } = true;

    public AutopilotScene Scene => _scene;

    public AutopilotDecision Decide(AutopilotInputs input)
    {
        double dt = _lastTick == DateTime.MinValue
            ? 1.0
            : Math.Clamp((input.LocalNow - _lastTick).TotalSeconds, 0.1, 30);
        _lastTick = input.LocalNow;

        _cpuLoadEma = Ema(_cpuLoadEma, Clamp100(input.CpuLoadPercent));
        _gpuLoadEma = Ema(_gpuLoadEma, Clamp100(input.GpuLoadPercent));

        UpdateScene(input);

        var (curveName, curve) = CurveFor(Goal, _scene);
        double target = SystemTarget(curve, input.CpuTempC, input.GpuTempC, _scene);
        // Protect is not eased in: full airflow on the same poll.
        _systemPercent = _scene == AutopilotScene.Protect ? 100 : Slew(_systemPercent, target, dt);

        return new AutopilotDecision(_scene, curveName, curve, _systemPercent,
            Headline(_scene, input.GameName), Reason(_scene, curveName, input));
    }

    public void Reset()
    {
        _scene = _candidate = AutopilotScene.Everyday;
        _candidateSince = _lastTick = DateTime.MinValue;
        _cpuLoadEma = _gpuLoadEma = _systemPercent = double.NaN;
        _started = false;
    }

    // ── Scene ────────────────────────────────────────────────────────────────

    private void UpdateScene(AutopilotInputs input)
    {
        bool first = !_started;
        _started = true;

        bool cpuHot = input.CpuTempC is double c && c >= ProtectCpuTempC;
        bool gpuHot = input.GpuTempC is double g && g >= ProtectGpuTempC;
        if (cpuHot || gpuHot)
        {
            // Safety first: no dwell on the way in.
            _scene = _candidate = AutopilotScene.Protect;
            _candidateSince = input.LocalNow;
            return;
        }

        if (_scene == AutopilotScene.Protect)
        {
            bool cpuStillWarm = input.CpuTempC is double c2 && c2 > ProtectCpuTempC - ProtectReleaseDeltaC;
            bool gpuStillWarm = input.GpuTempC is double g2 && g2 > ProtectGpuTempC - ProtectReleaseDeltaC;
            if (cpuStillWarm || gpuStillWarm) return;
        }

        var wanted = Classify(input);
        if (wanted == _scene)
        {
            _candidate = wanted;
            return;
        }

        if (wanted != _candidate)
        {
            _candidate = wanted;
            _candidateSince = input.LocalNow;
        }

        // First decision ever, or the candidate has held long enough.
        if (first || input.LocalNow - _candidateSince >= SceneDwell || _scene == AutopilotScene.Protect)
            _scene = wanted;
    }

    private AutopilotScene Classify(AutopilotInputs input)
    {
        if (input.GameMode) return AutopilotScene.Gaming;
        if (_cpuLoadEma >= HeavyWorkCpuLoadPercent) return AutopilotScene.HeavyWork;
        if (QuietHoursEnabled && IsQuietHour(input.LocalNow)) return AutopilotScene.QuietHours;

        bool cool = input.CpuTempC is not double c || c < IdleCpuTempC;
        if (cool && _cpuLoadEma < IdleLoadPercent && _gpuLoadEma < IdleLoadPercent) return AutopilotScene.Idle;
        return AutopilotScene.Everyday;
    }

    internal static bool IsQuietHour(DateTime localNow) => localNow.Hour >= QuietHoursStart || localNow.Hour < QuietHoursEnd;

    // ── Curves ───────────────────────────────────────────────────────────────

    /// <summary>The built-in curve for a goal in a scene.</summary>
    public static (string Name, List<FanCurvePoint> Curve) CurveFor(AutopilotGoal goal, AutopilotScene scene)
    {
        string name = (goal, scene) switch
        {
            (_, AutopilotScene.Protect) => goal == AutopilotGoal.Cool ? "Overclock" : "Performance",

            (AutopilotGoal.Quiet, AutopilotScene.Gaming) => "Balanced",
            (AutopilotGoal.Quiet, AutopilotScene.HeavyWork) => "Balanced",
            (AutopilotGoal.Quiet, _) => "Silent",

            (AutopilotGoal.Balanced, AutopilotScene.Idle) => "Silent",
            (AutopilotGoal.Balanced, AutopilotScene.QuietHours) => "Silent",
            (AutopilotGoal.Balanced, AutopilotScene.Gaming) => "Gaming",
            (AutopilotGoal.Balanced, AutopilotScene.HeavyWork) => "Workstation",
            (AutopilotGoal.Balanced, _) => "Balanced",

            (AutopilotGoal.Cool, AutopilotScene.Idle) => "Balanced",
            (AutopilotGoal.Cool, AutopilotScene.QuietHours) => "Balanced",
            (AutopilotGoal.Cool, _) => "Performance",

            _ => "Balanced",
        };

        List<FanCurvePoint> curve = name switch
        {
            "Silent" => SmartFanCurveEngine.GenerateSilentCurve(),
            "Performance" => SmartFanCurveEngine.GeneratePerformanceCurve(),
            "Gaming" => SmartFanCurveEngine.GenerateGamingCurve(),
            "Workstation" => SmartFanCurveEngine.GenerateWorkstationCurve(),
            "Overclock" => SmartFanCurveEngine.GenerateOverclockCurve(),
            _ => SmartFanCurveEngine.GenerateDefaultCurve(),
        };
        return (name, curve);
    }

    // ── System fans ──────────────────────────────────────────────────────────

    /// <summary>Unsmoothed system-fan duty for the combined heat of CPU and GPU on the scene's curve.</summary>
    internal static double SystemTarget(List<FanCurvePoint> curve, double? cpuTempC, double? gpuTempC, AutopilotScene scene)
    {
        if (scene == AutopilotScene.Protect) return 100;

        double heat = double.NaN;
        if (cpuTempC is double c && double.IsFinite(c)) heat = c;
        if (gpuTempC is double g && double.IsFinite(g))
            heat = double.IsNaN(heat) ? g - GpuTempOffsetC : Math.Max(heat, g - GpuTempOffsetC);
        if (double.IsNaN(heat)) return 50; // no temperature at all: a safe middle, never "off"

        var points = curve.OrderBy(p => p.TemperatureC).Select(p => new CurvePoint(p.TemperatureC, p.FanPercent)).ToList();
        double duty = FanCurveMath.Interpolate(points, heat);
        if (!double.IsFinite(duty)) return 50;
        return Math.Clamp(duty * SystemFanShare, MinSystemFanPercent, 100);
    }

    private static double Slew(double current, double target, double dt)
    {
        if (double.IsNaN(current)) return target;
        double diff = target - current;
        if (Math.Abs(diff) < HysteresisPercent && target < 100) return current;
        double maxStep = (diff > 0 ? RampUpPerSecond : RampDownPerSecond) * dt;
        return Math.Clamp(current + Math.Clamp(diff, -maxStep, maxStep), MinSystemFanPercent, 100);
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    private static string Headline(AutopilotScene scene, string gameName) => scene switch
    {
        AutopilotScene.Idle => "Idle",
        AutopilotScene.Gaming => string.IsNullOrWhiteSpace(gameName) ? "Gaming" : $"Gaming — {gameName}",
        AutopilotScene.HeavyWork => "Heavy work",
        AutopilotScene.QuietHours => "Quiet hours",
        AutopilotScene.Protect => "Protecting — running hot",
        _ => "Everyday use",
    };

    private string Reason(AutopilotScene scene, string curveName, AutopilotInputs input) => scene switch
    {
        AutopilotScene.Idle => $"Nothing demanding is running, so fans follow the {curveName} curve.",
        AutopilotScene.Gaming => $"A game is running — the {curveName} curve ramps early on CPU and GPU heat.",
        AutopilotScene.HeavyWork => $"CPU load has averaged {_cpuLoadEma:F0}% — the {curveName} curve holds steady cooling for long jobs.",
        AutopilotScene.QuietHours => $"It is between {QuietHoursStart}:00 and 0{QuietHoursEnd}:00 — the {curveName} curve keeps the PC quiet.",
        AutopilotScene.Protect => $"{HottestPart(input)} — all fans at full speed until it has cooled by {ProtectReleaseDeltaC:F0} °C.",
        _ => $"Light, mixed load — the {curveName} curve.",
    };

    private static string HottestPart(AutopilotInputs input)
    {
        if (input.GpuTempC is double g && g >= ProtectGpuTempC - ProtectReleaseDeltaC
            && (input.CpuTempC is not double c0 || g - ProtectGpuTempC >= c0 - ProtectCpuTempC))
            return $"GPU at {g:F0} °C";
        return input.CpuTempC is double c ? $"CPU at {c:F0} °C" : "A component is near its limit";
    }

    private static double Ema(double current, double sample)
        => double.IsNaN(current) ? sample : current + LoadEmaAlpha * (sample - current);

    private static double Clamp100(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
}
