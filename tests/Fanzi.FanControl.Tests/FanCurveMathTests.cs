using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Tests;

public class FanCurveMathTests
{
    private static IReadOnlyList<CurvePoint> Curve(params (double T, double D)[] points)
        => points.Select(p => new CurvePoint(p.T, p.D)).ToArray();

    private static readonly IReadOnlyList<CurvePoint> Simple = Curve((30, 20), (60, 50), (80, 100));

    private static void AssertStrictlyIncreasingAndInRange(IReadOnlyList<CurvePoint> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            Assert.InRange(points[i].TemperatureC, FanCurveMath.MinTemperatureC, FanCurveMath.MaxTemperatureC);
            Assert.InRange(points[i].DutyPercent, FanCurveMath.MinDutyPercent, FanCurveMath.MaxDutyPercent);
            if (i > 0) Assert.True(points[i].TemperatureC - points[i - 1].TemperatureC >= FanCurveMath.MinGapC, $"point {i} breaks monotonic X");
        }
    }

    // ── Interpolation ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(30, 20)]    // on a point
    [InlineData(45, 35)]    // halfway 30→60
    [InlineData(70, 75)]    // halfway 60→80
    [InlineData(80, 100)]   // last point
    [InlineData(10, 20)]    // below the curve: flat at the first duty
    [InlineData(95, 100)]   // above the curve: flat at the last duty
    public void Interpolate_IsLinearBetweenPointsAndFlatPastTheEnds(double temp, double expected)
        => Assert.Equal(expected, FanCurveMath.Interpolate(Simple, temp), 9);

    [Fact]
    public void Interpolate_EmptyCurveOrNaN_ReturnsNaN()
    {
        Assert.True(double.IsNaN(FanCurveMath.Interpolate(Array.Empty<CurvePoint>(), 50)));
        Assert.True(double.IsNaN(FanCurveMath.Interpolate(Simple, double.NaN)));
    }

    [Fact]
    public void Interpolate_DuplicateTemperature_NeverDividesByZero()
    {
        var dup = Curve((40, 30), (60, 40), (60, 70), (80, 90));
        double at60 = FanCurveMath.Interpolate(dup, 60);
        Assert.True(double.IsFinite(at60));
        Assert.Equal(40, at60, 9);                              // the first segment that reaches 60 °C
        Assert.Equal(35, FanCurveMath.Interpolate(dup, 50), 9); // halfway along 40→60 °C (30→40 %)
    }

    [Fact]
    public void Engine_FollowsTheSameInterpolation()
    {
        // AcousticFirst off: the engine moves 30 % of the way from 50 % toward the curve value each tick.
        var engine = new SmartFanCurveEngine { AcousticFirstEnabled = false, DeltaTFusionEnabled = false };
        var model = FanCurveMath.ToModel(Simple);
        double first = engine.ComputeOptimalFanSpeed(70, 20, 1000, model);
        Assert.Equal(50 + (FanCurveMath.Interpolate(Simple, 70) - 50) * 0.3, first, 6);
    }

    // ── Normalize ─────────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_SortsSnapsClampsAndDropsClashes()
    {
        var stored = new List<FanCurvePoint>
        {
            new() { TemperatureC = 70.4, FanPercent = 64.6 }, // snaps onto 70 °C, already taken by 70.2 → dropped
            new() { TemperatureC = 0, FanPercent = -5 },      // below range → 20 °C / 0 %
            new() { TemperatureC = 120, FanPercent = 150 },   // above range → 100 °C / 100 %
            new() { TemperatureC = 70.2, FanPercent = 10 },   // lower temperature, so it wins 70 °C
            new() { TemperatureC = double.NaN, FanPercent = 50 },
            new() { TemperatureC = 45, FanPercent = double.PositiveInfinity },
        };

        var result = FanCurveMath.Normalize(stored);

        Assert.Equal(Curve((20, 0), (70, 10), (100, 100)), result);
        AssertStrictlyIncreasingAndInRange(result);
    }

    [Fact]
    public void Normalize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(FanCurveMath.Normalize(null));
        Assert.Empty(FanCurveMath.Normalize(new List<FanCurvePoint>()));
    }

    // ── Move / nudge ──────────────────────────────────────────────────────────

    [Fact]
    public void MovePoint_SnapsToWholeDegreesAndPercent()
    {
        var moved = FanCurveMath.MovePoint(Simple, 1, 55.6, 42.4);
        Assert.Equal(new CurvePoint(56, 42), moved[1]);
        Assert.Equal(Simple[0], moved[0]);
        Assert.Equal(Simple[2], moved[2]);
        Assert.Equal(new CurvePoint(60, 50), Simple[1]); // input untouched
    }

    [Theory]
    [InlineData(1, 10, 31)]    // can't pass the previous point (30 °C) — stops 1 °C after it
    [InlineData(1, 95, 79)]    // can't pass the next point (80 °C) — stops 1 °C before it
    [InlineData(0, -40, 20)]   // first point stops at the range start
    [InlineData(2, 140, 100)]  // last point stops at the range end
    public void MovePoint_KeepsTemperatureMonotonicAndInRange(int index, double temp, double expected)
    {
        var moved = FanCurveMath.MovePoint(Simple, index, temp, 50);
        Assert.Equal(expected, moved[index].TemperatureC);
        AssertStrictlyIncreasingAndInRange(moved);
    }

    [Theory]
    [InlineData(-30, 0)]
    [InlineData(250, 100)]
    [InlineData(double.NaN, 0)]
    public void MovePoint_ClampsDuty(double duty, double expected)
        => Assert.Equal(expected, FanCurveMath.MovePoint(Simple, 1, 60, duty)[1].DutyPercent);

    [Fact]
    public void MovePoint_NoChangeOrBadIndex_ReturnsSameInstance()
    {
        Assert.Same(Simple, FanCurveMath.MovePoint(Simple, 1, 60.2, 49.8));
        Assert.Same(Simple, FanCurveMath.MovePoint(Simple, -1, 50, 50));
        Assert.Same(Simple, FanCurveMath.MovePoint(Simple, 3, 50, 50));
    }

    [Fact]
    public void NudgePoint_MovesByDeltaAndStopsAtNeighbours()
    {
        var right = FanCurveMath.NudgePoint(Simple, 1, 1, 0);
        Assert.Equal(new CurvePoint(61, 50), right[1]);

        var up = FanCurveMath.NudgePoint(Simple, 1, 0, 5);
        Assert.Equal(new CurvePoint(60, 55), up[1]);

        var tight = Curve((30, 20), (31, 30), (80, 100));
        Assert.Same(tight, FanCurveMath.NudgePoint(tight, 1, -1, 0)); // already 1 °C after its neighbour

        var top = FanCurveMath.NudgePoint(Simple, 2, 0, 5);
        Assert.Same(Simple, top); // 100 % can't go higher
    }

    // ── Insert / remove ───────────────────────────────────────────────────────

    [Fact]
    public void InsertPoint_KeepsTemperatureOrderAndSnaps()
    {
        var (points, index) = FanCurveMath.InsertPoint(Simple, 69.6, 70.3);
        Assert.Equal(2, index);
        Assert.Equal(Curve((30, 20), (60, 50), (70, 70), (80, 100)), points);
        AssertStrictlyIncreasingAndInRange(points);

        var (start, startIndex) = FanCurveMath.InsertPoint(Simple, 5, -10); // clamps into range, goes first
        Assert.Equal(0, startIndex);
        Assert.Equal(new CurvePoint(20, 0), start[0]);
    }

    [Fact]
    public void InsertPoint_TakenTemperatureOrFullCurve_IsRejected()
    {
        var (same, index) = FanCurveMath.InsertPoint(Simple, 60.4, 10);
        Assert.Equal(-1, index);
        Assert.Same(Simple, same);

        var full = Enumerable.Range(0, FanCurveMath.MaxPoints).Select(i => new CurvePoint(20 + i * 5, i * 5)).ToArray();
        var (unchanged, fullIndex) = FanCurveMath.InsertPoint(full, 92, 90);
        Assert.Equal(-1, fullIndex);
        Assert.Same(full, unchanged);
    }

    [Fact]
    public void InsertPoint_IntoEmptyCurve_Works()
    {
        var (points, index) = FanCurveMath.InsertPoint(Array.Empty<CurvePoint>(), 50, 40);
        Assert.Equal(0, index);
        Assert.Equal(Curve((50, 40)), points);
    }

    [Fact]
    public void RemovePoint_KeepsAtLeastTwoPoints()
    {
        var removed = FanCurveMath.RemovePoint(Simple, 1);
        Assert.Equal(Curve((30, 20), (80, 100)), removed);
        Assert.Same(removed, FanCurveMath.RemovePoint(removed, 0)); // minimum reached
        Assert.Same(Simple, FanCurveMath.RemovePoint(Simple, 5));
    }

    // ── Presets ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FanCurvePreset.Silent)]
    [InlineData(FanCurvePreset.Balanced)]
    [InlineData(FanCurvePreset.Performance)]
    [InlineData(FanCurvePreset.Full)]
    public void Preset_IsAValidEditorCurveAndIsRecognised(FanCurvePreset preset)
    {
        var points = FanCurveMath.Preset(preset);
        Assert.InRange(points.Count, FanCurveMath.MinPoints, FanCurveMath.MaxPoints);
        AssertStrictlyIncreasingAndInRange(points);
        Assert.Equal(preset, FanCurveMath.MatchPreset(points));
        // Through the stored form and back (what a save + reload does).
        Assert.Equal(preset, FanCurveMath.MatchPreset(FanCurveMath.Normalize(FanCurveMath.ToModel(points))));
    }

    [Fact]
    public void Presets_MatchTheEngineCurvesAndOrderByAggressiveness()
    {
        Assert.Equal(FanCurveMath.Normalize(SmartFanCurveEngine.GenerateDefaultCurve()), FanCurveMath.Preset(FanCurvePreset.Balanced));
        Assert.Equal(FanCurveMath.Normalize(SmartFanCurveEngine.GenerateSilentCurve()), FanCurveMath.Preset(FanCurvePreset.Silent));
        Assert.Equal(FanCurveMath.Normalize(SmartFanCurveEngine.GeneratePerformanceCurve()), FanCurveMath.Preset(FanCurvePreset.Performance));

        foreach (double t in new[] { 40.0, 60, 75 })
        {
            double silent = FanCurveMath.Interpolate(FanCurveMath.Preset(FanCurvePreset.Silent), t);
            double balanced = FanCurveMath.Interpolate(FanCurveMath.Preset(FanCurvePreset.Balanced), t);
            double performance = FanCurveMath.Interpolate(FanCurveMath.Preset(FanCurvePreset.Performance), t);
            double full = FanCurveMath.Interpolate(FanCurveMath.Preset(FanCurvePreset.Full), t);
            Assert.True(silent <= balanced && balanced <= performance && performance <= full, $"preset order broken at {t} °C");
            Assert.Equal(100, full);
        }
    }

    [Fact]
    public void MatchPreset_EditedCurve_IsCustom()
    {
        var edited = FanCurveMath.NudgePoint(FanCurveMath.Preset(FanCurvePreset.Balanced), 2, 0, 1);
        Assert.Null(FanCurveMath.MatchPreset(edited));
        Assert.Null(FanCurveMath.MatchPreset(null));
    }

    [Fact]
    public void ToModel_RoundTripsThroughNormalize()
    {
        var model = FanCurveMath.ToModel(Simple);
        Assert.Equal(new[] { 30.0, 60, 80 }, model.Select(p => p.TemperatureC));
        Assert.Equal(new[] { 20.0, 50, 100 }, model.Select(p => p.FanPercent));
        Assert.Equal(Simple, FanCurveMath.Normalize(model));
    }
}
