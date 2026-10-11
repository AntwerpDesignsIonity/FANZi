using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Controls;

namespace Fanzi.FanControl.Tests;

/// <summary>Mouse and keyboard editing of <see cref="FanCurveEditor"/> through real (headless) input.</summary>
public class FanCurveEditorTests
{
    private static readonly IReadOnlyList<CurvePoint> Balanced = FanCurveMath.Preset(FanCurvePreset.Balanced);

    private sealed class Harness : IDisposable
    {
        public Harness(IReadOnlyList<CurvePoint> points)
        {
            Editor = new FanCurveEditor
            {
                Points = points,
                Width = 500,
                Height = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Editor.PropertyChanged += (_, e) => { if (e.Property == FanCurveEditor.PointsProperty) Commits++; };
            Window = new Window { Content = Editor, Width = 600, Height = 400 };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public FanCurveEditor Editor { get; }
        public Window Window { get; }
        public int Commits { get; private set; }

        public Rect Plot => FanCurveEditor.PlotRect(Editor.Bounds.Size);

        /// <summary>Window coordinates of a curve value.</summary>
        public Point At(double tempC, double duty) => ToWindow(FanCurveEditor.ToPixel(new CurvePoint(tempC, duty), Plot));

        public Point ToWindow(Point local) => Editor.TranslatePoint(local, Window)!.Value;

        public void Dispose() => Window.Close();
    }

    [AvaloniaFact]
    public void Drag_MovesThePointSnappedAndCommitsOnceOnRelease()
    {
        using var h = new Harness(Balanced);
        int before = h.Commits;

        h.Window.MouseDown(h.At(60, 50), MouseButton.Left);
        Assert.True(h.Editor.IsDragging);
        Assert.Equal(2, h.Editor.SelectedIndex);

        h.Window.MouseMove(h.At(62.2, 55.1));
        h.Window.MouseMove(h.At(64.3, 57.6));
        Assert.Same(Balanced, h.Editor.Points); // preview only while the button is down
        Assert.Equal(new CurvePoint(64, 58), h.Editor.DisplayPoints[2]);

        h.Window.MouseUp(h.At(64.3, 57.6), MouseButton.Left);
        Assert.False(h.Editor.IsDragging);
        Assert.Equal(new CurvePoint(64, 58), h.Editor.Points![2]);
        Assert.Equal(before + 1, h.Commits);
        Assert.Equal(Balanced.Count, h.Editor.Points.Count);
    }

    [AvaloniaFact]
    public void Drag_PastANeighbour_StopsOneDegreeBeforeIt()
    {
        using var h = new Harness(Balanced);
        h.Window.MouseDown(h.At(60, 50), MouseButton.Left);
        h.Window.MouseMove(h.At(95, 120));   // way past the 70 °C neighbour and above the plot
        h.Window.MouseUp(h.At(95, 120), MouseButton.Left);

        var p = h.Editor.Points!;
        Assert.Equal(new CurvePoint(69, 100), p[2]);
        for (int i = 1; i < p.Count; i++) Assert.True(p[i].TemperatureC > p[i - 1].TemperatureC);
    }

    [AvaloniaFact]
    public void RightClick_RemovesAPoint_ButKeepsAtLeastTwo()
    {
        using var h = new Harness(FanCurveMath.Preset(FanCurvePreset.Silent).Take(3).ToArray());
        var p = h.Editor.Points!;

        h.Window.MouseDown(h.At(p[1].TemperatureC, p[1].DutyPercent), MouseButton.Right);
        h.Window.MouseUp(h.At(p[1].TemperatureC, p[1].DutyPercent), MouseButton.Right);
        Assert.Equal(new[] { p[0], p[2] }, h.Editor.Points);

        h.Window.MouseDown(h.At(p[0].TemperatureC, p[0].DutyPercent), MouseButton.Right);
        h.Window.MouseUp(h.At(p[0].TemperatureC, p[0].DutyPercent), MouseButton.Right);
        Assert.Equal(2, h.Editor.Points!.Count);
    }

    [AvaloniaFact]
    public void DoubleClick_OnEmptySpace_AddsASnappedPoint()
    {
        using var h = new Harness(Balanced);
        var where = h.At(52.4, 20.2); // well below the curve, away from every point

        h.Window.MouseDown(where, MouseButton.Left);
        h.Window.MouseUp(where, MouseButton.Left);
        Assert.Equal(Balanced.Count, h.Editor.Points!.Count); // a single click only deselects
        h.Window.MouseDown(where, MouseButton.Left);
        h.Window.MouseUp(where, MouseButton.Left);

        var p = h.Editor.Points!;
        Assert.Equal(Balanced.Count + 1, p.Count);
        Assert.Equal(new CurvePoint(52, 20), p[2]); // between the 45 °C and 60 °C points
        Assert.Equal(2, h.Editor.SelectedIndex);
    }

    [AvaloniaFact]
    public void Keyboard_ArrowsNudgeTheSelectedPoint()
    {
        using var h = new Harness(Balanced);
        h.Editor.Focus();

        h.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(0, h.Editor.SelectedIndex); // first arrow picks a point
        Assert.Same(Balanced, h.Editor.Points);

        h.Window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
        h.Window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
        Assert.Equal(2, h.Editor.SelectedIndex);

        h.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(new CurvePoint(61, 50), h.Editor.Points![2]);
        h.Window.KeyPress(Key.Up, RawInputModifiers.Shift, PhysicalKey.ArrowUp, null);
        Assert.Equal(new CurvePoint(61, 55), h.Editor.Points![2]);
        h.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(new CurvePoint(61, 54), h.Editor.Points![2]);

        // Shift+Right ×3 runs into the 70 °C neighbour and stops 1 °C short.
        for (int i = 0; i < 3; i++)
            h.Window.KeyPress(Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight, null);
        Assert.Equal(69, h.Editor.Points![2].TemperatureC);

        h.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Assert.Equal(Balanced.Count - 1, h.Editor.Points!.Count);
        Assert.Equal(new CurvePoint(70, 65), h.Editor.Points![2]); // the next point is now selected
        Assert.Equal(2, h.Editor.SelectedIndex);
    }

    [AvaloniaFact]
    public void Marker_ShowsTheCurveDutyForTheCurrentTemperature()
    {
        using var h = new Harness(Balanced);
        Assert.Null(h.Editor.MarkerPoint);

        h.Editor.CurrentTemperature = 75;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(72.5, h.Editor.MarkerDuty!.Value, 9);
        Assert.Equal(FanCurveEditor.ToPixel(new CurvePoint(75, 72.5), h.Plot), h.Editor.MarkerPoint);

        h.Editor.CurrentTemperature = 110; // past the range: pinned to the right edge at the last duty
        Assert.Equal(100, h.Editor.MarkerDuty!.Value);
        Assert.Equal(h.Plot.Right, h.Editor.MarkerPoint!.Value.X, 6);
    }

    [Fact]
    public void Geometry_PixelRoundTripAndHitTest()
    {
        var plot = new Rect(40, 10, 400, 200);
        Assert.Equal(new Point(40, 210), FanCurveEditor.ToPixel(new CurvePoint(20, 0), plot));
        Assert.Equal(new Point(440, 10), FanCurveEditor.ToPixel(new CurvePoint(100, 100), plot));

        var back = FanCurveEditor.FromPixel(FanCurveEditor.ToPixel(new CurvePoint(63, 41), plot), plot);
        Assert.Equal(63, back.TemperatureC, 9);
        Assert.Equal(41, back.DutyPercent, 9);

        var centre = FanCurveEditor.ToPixel(Balanced[3], plot);
        Assert.Equal(3, FanCurveEditor.HitTest(Balanced, plot, centre + new Point(4, -4)));
        Assert.Equal(-1, FanCurveEditor.HitTest(Balanced, plot, centre + new Point(0, 40)));
    }
}
