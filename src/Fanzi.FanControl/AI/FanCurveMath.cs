using Fanzi.FanControl.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

/// <summary>One fan-curve point: CPU temperature (°C) → fan duty (%).</summary>
public readonly record struct CurvePoint(double TemperatureC, double DutyPercent);

/// <summary>Built-in curves offered by the Fans-tab curve editor.</summary>
public enum FanCurvePreset
{
    Silent,
    Balanced,
    Performance,
    Full,
}

/// <summary>
/// Pure fan-curve maths shared by <see cref="SmartFanCurveEngine"/> and the curve editor: linear
/// interpolation, snapping (1 °C / 1 %), clamping to 20–100 °C × 0–100 %, strictly increasing
/// temperatures, point insert/move/remove and the presets. Every edit returns a new list.
/// </summary>
public static class FanCurveMath
{
    public const double MinTemperatureC = 20;
    public const double MaxTemperatureC = 100;
    public const double MinDutyPercent = 0;
    public const double MaxDutyPercent = 100;

    /// <summary>Smallest temperature gap between neighbouring points (one snap step).</summary>
    public const double MinGapC = 1;

    /// <summary>A curve always keeps at least two points (the engine ignores shorter curves).</summary>
    public const int MinPoints = 2;

    /// <summary>Upper bound on points so the editor stays usable.</summary>
    public const int MaxPoints = 12;

    /// <summary>Snaps a temperature to whole °C inside the editor range.</summary>
    public static double SnapTemperature(double tempC)
        => Math.Clamp(Math.Round(Finite(tempC, MinTemperatureC), MidpointRounding.AwayFromZero), MinTemperatureC, MaxTemperatureC);

    /// <summary>Snaps a duty to whole % inside 0–100.</summary>
    public static double SnapDuty(double duty)
        => Math.Clamp(Math.Round(Finite(duty, MinDutyPercent), MidpointRounding.AwayFromZero), MinDutyPercent, MaxDutyPercent);

    /// <summary>
    /// Linear interpolation over points sorted by temperature; flat before the first and after the last
    /// point (the fan never goes beyond the curve's end values). NaN for an empty curve or a NaN input.
    /// </summary>
    public static double Interpolate(IReadOnlyList<CurvePoint> points, double tempC)
    {
        if (points is null || points.Count == 0 || double.IsNaN(tempC)) return double.NaN;
        if (tempC <= points[0].TemperatureC) return points[0].DutyPercent;
        if (tempC >= points[^1].TemperatureC) return points[^1].DutyPercent;

        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (tempC > b.TemperatureC) continue;
            double range = b.TemperatureC - a.TemperatureC;
            if (range <= 0) return b.DutyPercent; // duplicate temperature: take the later point
            double t = (tempC - a.TemperatureC) / range;
            return a.DutyPercent + t * (b.DutyPercent - a.DutyPercent);
        }

        return points[^1].DutyPercent;
    }

    /// <summary>
    /// Editor form of a stored curve: non-finite points dropped, values snapped and clamped, sorted by
    /// temperature, and points that land on an already-used temperature dropped (first one wins).
    /// May return fewer than <see cref="MinPoints"/> points; callers fall back to a preset then.
    /// </summary>
    public static IReadOnlyList<CurvePoint> Normalize(IEnumerable<FanCurvePoint>? points)
    {
        if (points is null) return Array.Empty<CurvePoint>();

        var result = new List<CurvePoint>();
        foreach (var p in points
                     .Where(p => p is not null && double.IsFinite(p.TemperatureC) && double.IsFinite(p.FanPercent))
                     .OrderBy(p => p.TemperatureC))
        {
            var snapped = new CurvePoint(SnapTemperature(p.TemperatureC), SnapDuty(p.FanPercent));
            if (result.Count > 0 && snapped.TemperatureC - result[^1].TemperatureC < MinGapC) continue;
            result.Add(snapped);
        }
        return result;
    }

    /// <summary>
    /// Moves point <paramref name="index"/> to the snapped position, kept strictly between its neighbours
    /// (at least <see cref="MinGapC"/> away) and inside the editor range. Out-of-range index → unchanged.
    /// </summary>
    public static IReadOnlyList<CurvePoint> MovePoint(IReadOnlyList<CurvePoint> points, int index, double tempC, double duty)
    {
        if (points is null || index < 0 || index >= points.Count) return points ?? Array.Empty<CurvePoint>();

        double low = index > 0 ? points[index - 1].TemperatureC + MinGapC : MinTemperatureC;
        double high = index < points.Count - 1 ? points[index + 1].TemperatureC - MinGapC : MaxTemperatureC;
        low = Math.Max(low, MinTemperatureC);
        high = Math.Min(high, MaxTemperatureC);

        double t = SnapTemperature(tempC);
        t = high >= low ? Math.Clamp(t, low, high) : points[index].TemperatureC;
        var moved = new CurvePoint(t, SnapDuty(duty));
        if (moved == points[index]) return points;

        var copy = points.ToArray();
        copy[index] = moved;
        return copy;
    }

    /// <summary>Moves point <paramref name="index"/> by a delta (keyboard nudge); same rules as <see cref="MovePoint"/>.</summary>
    public static IReadOnlyList<CurvePoint> NudgePoint(IReadOnlyList<CurvePoint> points, int index, double deltaC, double deltaDuty)
    {
        if (points is null || index < 0 || index >= points.Count) return points ?? Array.Empty<CurvePoint>();
        var p = points[index];
        return MovePoint(points, index, p.TemperatureC + deltaC, p.DutyPercent + deltaDuty);
    }

    /// <summary>
    /// Inserts a snapped point at its sorted position. Returns the new list and the new point's index,
    /// or the unchanged list and -1 when the temperature is already taken or the curve is full.
    /// </summary>
    public static (IReadOnlyList<CurvePoint> Points, int Index) InsertPoint(IReadOnlyList<CurvePoint> points, double tempC, double duty)
    {
        points ??= Array.Empty<CurvePoint>();
        if (points.Count >= MaxPoints) return (points, -1);

        var point = new CurvePoint(SnapTemperature(tempC), SnapDuty(duty));
        int index = 0;
        while (index < points.Count && points[index].TemperatureC < point.TemperatureC) index++;

        bool clashesBefore = index > 0 && point.TemperatureC - points[index - 1].TemperatureC < MinGapC;
        bool clashesAfter = index < points.Count && points[index].TemperatureC - point.TemperatureC < MinGapC;
        if (clashesBefore || clashesAfter) return (points, -1);

        var list = points.ToList();
        list.Insert(index, point);
        return (list, index);
    }

    /// <summary>Removes point <paramref name="index"/> unless that would leave fewer than <see cref="MinPoints"/>.</summary>
    public static IReadOnlyList<CurvePoint> RemovePoint(IReadOnlyList<CurvePoint> points, int index)
    {
        if (points is null || points.Count <= MinPoints || index < 0 || index >= points.Count)
            return points ?? Array.Empty<CurvePoint>();

        var list = points.ToList();
        list.RemoveAt(index);
        return list;
    }

    /// <summary>Stored (profile) form of an editor curve.</summary>
    public static List<FanCurvePoint> ToModel(IReadOnlyList<CurvePoint> points)
        => points.Select(p => new FanCurvePoint { TemperatureC = p.TemperatureC, FanPercent = p.DutyPercent }).ToList();

    /// <summary>The preset's points in editor form.</summary>
    public static IReadOnlyList<CurvePoint> Preset(FanCurvePreset preset) => preset switch
    {
        FanCurvePreset.Silent => Normalize(SmartFanCurveEngine.GenerateSilentCurve()),
        FanCurvePreset.Performance => Normalize(SmartFanCurveEngine.GeneratePerformanceCurve()),
        FanCurvePreset.Full => new[] { new CurvePoint(MinTemperatureC, 100), new CurvePoint(MaxTemperatureC, 100) },
        _ => Normalize(SmartFanCurveEngine.GenerateDefaultCurve()),
    };

    /// <summary>The preset these points are exactly equal to, or null for a custom curve.</summary>
    public static FanCurvePreset? MatchPreset(IReadOnlyList<CurvePoint>? points)
    {
        if (points is null) return null;
        foreach (var preset in Enum.GetValues<FanCurvePreset>())
        {
            if (Preset(preset).SequenceEqual(points))
                return preset;
        }
        return null;
    }

    // ±∞ survives so it clamps to the matching end of the range; only NaN needs a stand-in.
    private static double Finite(double value, double fallback) => double.IsNaN(value) ? fallback : value;
}
