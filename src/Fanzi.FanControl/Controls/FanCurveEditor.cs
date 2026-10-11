using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// Interactive fan curve: CPU temperature (X, 20–100 °C) → fan duty (Y, 0–100 %).
/// <list type="bullet">
/// <item>Drag a point (snaps to 1 °C / 1 %, stays between its neighbours); the edit is committed to
/// <see cref="Points"/> on release, so a two-way binding sees one change per drag.</item>
/// <item>Double-click empty space to add a point; right-click a point to remove it (minimum two).</item>
/// <item>Keyboard: arrows nudge the selected point (Shift = 5), PageUp/PageDown or Home/End pick a point,
/// Delete removes it, Insert adds one after it, Escape cancels a drag.</item>
/// </list>
/// The curve is drawn as straight segments (the engine interpolates linearly) and runs flat past the end
/// points, stroked and filled with a horizontal <see cref="TemperatureColorScale"/> gradient. A live marker
/// shows <see cref="CurrentTemperature"/> and the duty the curve gives for it. Maths lives in <see cref="FanCurveMath"/>.
/// </summary>
public sealed class FanCurveEditor : Control
{
    public static readonly StyledProperty<IReadOnlyList<CurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<FanCurveEditor, IReadOnlyList<CurvePoint>?>(nameof(Points),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Live CPU temperature for the marker (null hides it).</summary>
    public static readonly StyledProperty<double?> CurrentTemperatureProperty =
        AvaloniaProperty.Register<FanCurveEditor, double?>(nameof(CurrentTemperature));

    /// <summary>Selected point (-1 = none); the keyboard acts on it.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<FanCurveEditor, int>(nameof(SelectedIndex), -1, defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Caption on the live marker chip (e.g. "CPU").</summary>
    public static readonly StyledProperty<string> MarkerLabelProperty =
        AvaloniaProperty.Register<FanCurveEditor, string>(nameof(MarkerLabel), "CPU");

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<FanCurveEditor, IBrush?>(nameof(LabelBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<FanCurveEditor, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> PlotBackgroundProperty =
        AvaloniaProperty.Register<FanCurveEditor, IBrush?>(nameof(PlotBackground));

    public static readonly StyledProperty<IBrush?> FocusBrushProperty =
        AvaloniaProperty.Register<FanCurveEditor, IBrush?>(nameof(FocusBrush));

    // Fallbacks when the theme style isn't applied (FanziTextMuted / grid / surface / accent).
    private static readonly IBrush DefaultLabelBrush = new SolidColorBrush(Color.Parse("#6A8AAF"));
    private static readonly IBrush DefaultGridBrush = new SolidColorBrush(Color.Parse("#1E2F4D"));
    private static readonly IBrush DefaultPlotBackground = new SolidColorBrush(Color.Parse("#070F1C"));
    private static readonly IBrush DefaultFocusBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush ChipBackground = new SolidColorBrush(Color.Parse("#E60A1426"));
    private static readonly IBrush ChipText = new SolidColorBrush(Color.Parse("#E8F0FF"));
    private static readonly IPen PointOutline = new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), 1.5);
    private static readonly IPen SelectedOutline = new Pen(Brushes.White, 2.25);

    /// <summary>Pointer distance (px) that grabs a point.</summary>
    internal const double HitRadius = 11;

    private const double LabelFontSize = 10;
    private const double ChipFontSize = 11;
    private const double TemperatureGrid = 10;
    private const double DutyGrid = 20;

    private static Cursor? _handCursor;

    private IReadOnlyList<CurvePoint>? _preview;   // drag in progress (null otherwise)
    private int _dragIndex = -1;
    private int _hoverIndex = -1;
    private bool _focusFromKeyboard;

    static FanCurveEditor()
    {
        FocusableProperty.OverrideDefaultValue<FanCurveEditor>(true);
        AffectsRender<FanCurveEditor>(PointsProperty, CurrentTemperatureProperty, SelectedIndexProperty,
            MarkerLabelProperty, LabelBrushProperty, GridBrushProperty, PlotBackgroundProperty, FocusBrushProperty);
    }

    public IReadOnlyList<CurvePoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double? CurrentTemperature { get => GetValue(CurrentTemperatureProperty); set => SetValue(CurrentTemperatureProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public string MarkerLabel { get => GetValue(MarkerLabelProperty); set => SetValue(MarkerLabelProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? PlotBackground { get => GetValue(PlotBackgroundProperty); set => SetValue(PlotBackgroundProperty, value); }
    public IBrush? FocusBrush { get => GetValue(FocusBrushProperty); set => SetValue(FocusBrushProperty, value); }

    /// <summary>What is drawn: the drag preview while dragging, otherwise <see cref="Points"/>.</summary>
    internal IReadOnlyList<CurvePoint> DisplayPoints => _preview ?? Points ?? Array.Empty<CurvePoint>();

    internal bool IsDragging => _dragIndex >= 0;

    /// <summary>Point centres in local coordinates (exposed for tests and the render harness).</summary>
    internal Point[] PointPositions
    {
        get
        {
            var plot = PlotRect(Bounds.Size);
            var pts = DisplayPoints;
            var result = new Point[pts.Count];
            for (int i = 0; i < pts.Count; i++) result[i] = ToPixel(pts[i], plot);
            return result;
        }
    }

    /// <summary>Live marker position on the curve (null when there is no temperature or curve).</summary>
    internal Point? MarkerPoint => MarkerValue() is { } m ? ToPixel(m, PlotRect(Bounds.Size)) : null;

    /// <summary>Duty the curve gives for <see cref="CurrentTemperature"/> (null when hidden).</summary>
    internal double? MarkerDuty => MarkerValue()?.DutyPercent;

    // ── Geometry (pure) ───────────────────────────────────────────────────────

    /// <summary>Plot area inside <paramref name="size"/>, leaving room for the % labels (left) and °C labels (bottom).</summary>
    internal static Rect PlotRect(Size size)
    {
        var pad = new Thickness(42, 12, 16, 24);
        return new Rect(pad.Left, pad.Top,
            Math.Max(0, size.Width - pad.Left - pad.Right),
            Math.Max(0, size.Height - pad.Top - pad.Bottom));
    }

    internal static Point ToPixel(CurvePoint point, Rect plot)
    {
        double tx = (Math.Clamp(point.TemperatureC, FanCurveMath.MinTemperatureC, FanCurveMath.MaxTemperatureC) - FanCurveMath.MinTemperatureC)
                    / (FanCurveMath.MaxTemperatureC - FanCurveMath.MinTemperatureC);
        double ty = Math.Clamp(point.DutyPercent, FanCurveMath.MinDutyPercent, FanCurveMath.MaxDutyPercent) / FanCurveMath.MaxDutyPercent;
        return new Point(plot.Left + tx * plot.Width, plot.Bottom - ty * plot.Height);
    }

    /// <summary>Unsnapped curve coordinates for a local position (snapping/clamping is FanCurveMath's job).</summary>
    internal static CurvePoint FromPixel(Point position, Rect plot)
    {
        double w = Math.Max(1, plot.Width), h = Math.Max(1, plot.Height);
        double temp = FanCurveMath.MinTemperatureC
                      + (position.X - plot.Left) / w * (FanCurveMath.MaxTemperatureC - FanCurveMath.MinTemperatureC);
        double duty = (plot.Bottom - position.Y) / h * FanCurveMath.MaxDutyPercent;
        return new CurvePoint(temp, duty);
    }

    /// <summary>Index of the point nearest <paramref name="position"/> within <paramref name="radius"/> px, else -1.</summary>
    internal static int HitTest(IReadOnlyList<CurvePoint> points, Rect plot, Point position, double radius = HitRadius)
    {
        int best = -1;
        double bestDistance = radius * radius;
        for (int i = 0; i < points.Count; i++)
        {
            var p = ToPixel(points[i], plot);
            double dx = p.X - position.X, dy = p.Y - position.Y;
            double d = dx * dx + dy * dy;
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    private CurvePoint? MarkerValue()
    {
        var pts = DisplayPoints;
        if (CurrentTemperature is not double t || !double.IsFinite(t) || pts.Count == 0) return null;
        double duty = FanCurveMath.Interpolate(pts, t);
        return double.IsFinite(duty) ? new CurvePoint(t, duty) : null;
    }

    // ── Editing ───────────────────────────────────────────────────────────────

    private void Commit(IReadOnlyList<CurvePoint> points)
    {
        if (!ReferenceEquals(points, Points))
            SetCurrentValue(PointsProperty, points);
    }

    private void Select(int index) => SetCurrentValue(SelectedIndexProperty, index);

    /// <summary>Adds a point at a local position (double-click); returns its index or -1.</summary>
    internal int InsertAt(Point position)
    {
        var c = FromPixel(position, PlotRect(Bounds.Size));
        var (points, index) = FanCurveMath.InsertPoint(Points ?? Array.Empty<CurvePoint>(), c.TemperatureC, c.DutyPercent);
        if (index < 0) return -1;
        Commit(points);
        Select(index);
        return index;
    }

    /// <summary>Removes point <paramref name="index"/> (right-click / Delete) when more than two remain.</summary>
    internal bool RemoveAt(int index)
    {
        var current = Points ?? Array.Empty<CurvePoint>();
        var points = FanCurveMath.RemovePoint(current, index);
        if (ReferenceEquals(points, current)) return false;
        Commit(points);
        Select(points.Count == 0 ? -1 : Math.Min(index, points.Count - 1));
        return true;
    }

    private void EndDrag(bool commit)
    {
        if (!IsDragging) return;
        var preview = _preview;
        _preview = null;
        _dragIndex = -1;
        if (commit && preview is not null) Commit(preview);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PointsProperty)
        {
            int count = Points?.Count ?? 0;
            if (SelectedIndex >= count) Select(count - 1);
            if (!IsDragging) _hoverIndex = -1;
        }
    }

    // ── Pointer ───────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var plot = PlotRect(Bounds.Size);
        var pts = Points ?? Array.Empty<CurvePoint>();
        int hit = HitTest(pts, plot, point.Position);
        Focus(NavigationMethod.Pointer);

        if (point.Properties.IsRightButtonPressed)
        {
            if (hit >= 0) RemoveAt(hit);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        if (hit >= 0)
        {
            Select(hit);
            _dragIndex = hit;
            _preview = pts;
            e.Pointer.Capture(this);
        }
        else if (e.ClickCount >= 2)
        {
            InsertAt(point.Position);
        }
        else
        {
            Select(-1);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var plot = PlotRect(Bounds.Size);
        var position = e.GetPosition(this);

        if (IsDragging && _preview is not null)
        {
            var c = FromPixel(position, plot);
            var moved = FanCurveMath.MovePoint(_preview, _dragIndex, c.TemperatureC, c.DutyPercent);
            if (!ReferenceEquals(moved, _preview))
            {
                _preview = moved;
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        int hover = HitTest(Points ?? Array.Empty<CurvePoint>(), plot, position);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Cursor = hover >= 0 ? HandCursor() : null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsDragging) return;
        EndDrag(commit: true);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag(commit: true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverIndex < 0) return;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    private static Cursor? HandCursor()
    {
        try { return _handCursor ??= new Cursor(StandardCursorType.Hand); }
        catch (Exception) { return null; } // no cursor factory (some headless hosts)
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var pts = Points ?? Array.Empty<CurvePoint>();
        if (pts.Count == 0) return;
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
        int selected = SelectedIndex;

        switch (e.Key)
        {
            case Key.Escape when IsDragging:
                EndDrag(commit: false);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when selected < 0 || selected >= pts.Count:
                Select(0); // first arrow press picks a point to work on
                break;
            case Key.Left:
                Commit(FanCurveMath.NudgePoint(pts, selected, -step, 0));
                break;
            case Key.Right:
                Commit(FanCurveMath.NudgePoint(pts, selected, step, 0));
                break;
            case Key.Up:
                Commit(FanCurveMath.NudgePoint(pts, selected, 0, step));
                break;
            case Key.Down:
                Commit(FanCurveMath.NudgePoint(pts, selected, 0, -step));
                break;
            case Key.PageUp:
                Select(selected <= 0 ? 0 : selected - 1);
                break;
            case Key.PageDown:
                Select(Math.Min(pts.Count - 1, selected + 1));
                break;
            case Key.Home:
                Select(0);
                break;
            case Key.End:
                Select(pts.Count - 1);
                break;
            case Key.Delete or Key.Back when selected >= 0:
                RemoveAt(selected);
                break;
            case Key.Insert or Key.Add or Key.OemPlus:
                InsertAfterSelected(pts, selected);
                break;
            default:
                return;
        }
        _focusFromKeyboard = true;
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>Insert: a point halfway to the next one (or the previous one at the end of the curve).</summary>
    private void InsertAfterSelected(IReadOnlyList<CurvePoint> pts, int selected)
    {
        int i = Math.Clamp(selected, 0, pts.Count - 1);
        var a = pts[i];
        var b = i + 1 < pts.Count ? pts[i + 1] : (i > 0 ? pts[i - 1] : new CurvePoint(FanCurveMath.MaxTemperatureC, a.DutyPercent));
        var (points, index) = FanCurveMath.InsertPoint(pts, (a.TemperatureC + b.TemperatureC) / 2, (a.DutyPercent + b.DutyPercent) / 2);
        if (index < 0) return;
        Commit(points);
        Select(index);
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _focusFromKeyboard = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _focusFromKeyboard = false;
        EndDrag(commit: true);
        InvalidateVisual();
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize) => new(0, 260);

    public override void Render(DrawingContext context)
    {
        var plot = PlotRect(Bounds.Size);
        if (plot.Width < 20 || plot.Height < 20) return;

        var typeface = new Typeface(TextElement.GetFontFamily(this));
        var labelBrush = LabelBrush ?? DefaultLabelBrush;

        context.DrawRectangle(PlotBackground ?? DefaultPlotBackground, null, plot, 8, 8);
        DrawGrid(context, plot, typeface, labelBrush);

        var pts = DisplayPoints;
        if (pts.Count >= 1)
        {
            var positions = new Point[pts.Count];
            for (int i = 0; i < pts.Count; i++) positions[i] = ToPixel(pts[i], plot);
            DrawCurve(context, plot, positions);
            DrawMarker(context, plot, typeface);
            DrawPoints(context, positions);
            DrawSelectedLabel(context, plot, pts, positions, typeface);
        }
        else
        {
            var text = Text("No curve — pick a preset", ChipFontSize, typeface, labelBrush);
            context.DrawText(text, new Point(plot.Center.X - text.Width / 2, plot.Center.Y - text.Height / 2));
        }

        if (IsFocused && _focusFromKeyboard)
        {
            var ring = new Pen(FocusBrush ?? DefaultFocusBrush, 2);
            context.DrawRectangle(null, ring, plot.Inflate(3), 10, 10);
        }
    }

    private void DrawGrid(DrawingContext context, Rect plot, Typeface typeface, IBrush labelBrush)
    {
        var gridBrush = GridBrush ?? DefaultGridBrush;
        var faint = new Pen(gridBrush, 1, new DashStyle(new double[] { 2, 4 }, 0));
        var solid = new Pen(gridBrush, 1);

        for (double d = 0; d <= FanCurveMath.MaxDutyPercent + 1e-9; d += DutyGrid)
        {
            double y = Math.Round(ToPixel(new CurvePoint(FanCurveMath.MinTemperatureC, d), plot).Y) + 0.5;
            context.DrawLine(d == 0 ? solid : faint, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = Text(d.ToString("F0", CultureInfo.CurrentCulture) + "%", LabelFontSize, typeface, labelBrush);
            context.DrawText(text, new Point(plot.Left - 6 - text.Width, y - text.Height / 2));
        }

        for (double t = FanCurveMath.MinTemperatureC; t <= FanCurveMath.MaxTemperatureC + 1e-9; t += TemperatureGrid)
        {
            double x = Math.Round(ToPixel(new CurvePoint(t, 0), plot).X) + 0.5;
            if (t > FanCurveMath.MinTemperatureC && t < FanCurveMath.MaxTemperatureC)
                context.DrawLine(faint, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var text = Text(t.ToString("F0", CultureInfo.CurrentCulture) + "°", LabelFontSize, typeface, labelBrush);
            double tx = Math.Clamp(x - text.Width / 2, plot.Left - 4, plot.Right - text.Width + 4);
            context.DrawText(text, new Point(tx, plot.Bottom + 5));
        }
    }

    private static void DrawCurve(DrawingContext context, Rect plot, Point[] positions)
    {
        // Flat before the first and after the last point — exactly what the engine does.
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(plot.Left, positions[0].Y), isFilled: false);
            foreach (var p in positions) g.LineTo(p);
            g.LineTo(new Point(plot.Right, positions[^1].Y));
            g.EndFigure(isClosed: false);
        }

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(plot.Left, plot.Bottom), isFilled: true);
            g.LineTo(new Point(plot.Left, positions[0].Y));
            foreach (var p in positions) g.LineTo(p);
            g.LineTo(new Point(plot.Right, positions[^1].Y));
            g.LineTo(new Point(plot.Right, plot.Bottom));
            g.EndFigure(isClosed: true);
        }

        // Fill: temperature colour along X, fading from ~55 % under the curve's peak to ~5 % at 0 %.
        double top = plot.Bottom;
        foreach (var p in positions) top = Math.Min(top, p.Y);
        if (plot.Bottom - top < 1) top = plot.Bottom - 1;
        var fade = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, top, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, plot.Bottom, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 0.55),
                new GradientStop(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF), 1),
            },
        };
        using (context.PushOpacityMask(fade, plot))
            context.DrawGeometry(TemperatureBrush(plot, 1), null, area);

        var glow = new Pen(TemperatureBrush(plot, 0.22), 9, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var stroke = new Pen(TemperatureBrush(plot, 1), 2.75, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, glow, line);
        context.DrawGeometry(null, stroke, line);
    }

    /// <summary>Horizontal gradient: each X takes the temperature colour of the °C drawn there.</summary>
    private static LinearGradientBrush TemperatureBrush(Rect plot, double alpha)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(plot.Left, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(plot.Right, 0, RelativeUnit.Absolute),
        };
        double span = FanCurveMath.MaxTemperatureC - FanCurveMath.MinTemperatureC;
        for (double t = FanCurveMath.MinTemperatureC; t <= FanCurveMath.MaxTemperatureC + 1e-9; t += 5)
        {
            brush.GradientStops.Add(new GradientStop(
                TemperatureColorScale.WithAlpha(TemperatureColorScale.ForTemperature(t), alpha),
                (t - FanCurveMath.MinTemperatureC) / span));
        }
        return brush;
    }

    private void DrawMarker(DrawingContext context, Rect plot, Typeface typeface)
    {
        if (MarkerValue() is not { } m) return;
        var p = ToPixel(m, plot);
        var color = TemperatureColorScale.ForTemperature(m.TemperatureC);
        var dashed = new Pen(new SolidColorBrush(TemperatureColorScale.WithAlpha(color, 0.7)), 1.25,
            new DashStyle(new double[] { 3, 3 }, 0));
        var dashedSoft = new Pen(new SolidColorBrush(TemperatureColorScale.WithAlpha(color, 0.4)), 1,
            new DashStyle(new double[] { 3, 3 }, 0));

        context.DrawLine(dashed, new Point(p.X, plot.Top), new Point(p.X, plot.Bottom));
        context.DrawLine(dashedSoft, new Point(plot.Left, p.Y), new Point(p.X, p.Y));

        var halo = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(TemperatureColorScale.WithAlpha(color, 0.8), 0),
                new GradientStop(TemperatureColorScale.WithAlpha(color, 0.3), 0.5),
                new GradientStop(TemperatureColorScale.WithAlpha(color, 0), 1),
            },
        };
        context.DrawEllipse(halo, null, p, 14, 14);
        context.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, 2), p, 5, 5);

        // Chip: "CPU 79° → 78%", right of the line (left when near the right edge), kept inside the plot.
        var bold = new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.Bold);
        var text = Text($"{MarkerLabel} {m.TemperatureC.ToString("F0", CultureInfo.CurrentCulture)}° → {m.DutyPercent.ToString("F0", CultureInfo.CurrentCulture)}%",
            ChipFontSize, bold, ChipText);
        var size = new Size(text.Width + 16, text.Height + 8);
        double x = p.X + 10 + size.Width <= plot.Right ? p.X + 10 : p.X - 10 - size.Width;
        double y = Math.Clamp(p.Y - size.Height - 12, plot.Top + 4, plot.Bottom - size.Height - 4);
        var chip = new Rect(new Point(Math.Max(plot.Left + 4, x), y), size);
        context.DrawRectangle(ChipBackground, new Pen(new SolidColorBrush(TemperatureColorScale.WithAlpha(color, 0.9)), 1.25), chip, 8, 8);
        context.DrawText(text, new Point(chip.X + 8, chip.Y + 4));
    }

    private void DrawPoints(DrawingContext context, Point[] positions)
    {
        var pts = DisplayPoints;
        int selected = SelectedIndex;
        for (int i = 0; i < positions.Length; i++)
        {
            var color = TemperatureColorScale.ForTemperature(pts[i].TemperatureC);
            bool isSelected = i == selected;
            bool isHover = i == _hoverIndex || i == _dragIndex;
            if (isSelected || isHover)
            {
                context.DrawEllipse(new SolidColorBrush(TemperatureColorScale.WithAlpha(color, isSelected ? 0.35 : 0.22)),
                    null, positions[i], 13, 13);
            }
            double r = isSelected ? 7.5 : isHover ? 7 : 6;
            context.DrawEllipse(new SolidColorBrush(color), isSelected ? SelectedOutline : PointOutline, positions[i], r, r);
        }
    }

    /// <summary>"65° · 70%" above the selected (or dragged) point.</summary>
    private void DrawSelectedLabel(DrawingContext context, Rect plot, IReadOnlyList<CurvePoint> pts, Point[] positions, Typeface typeface)
    {
        int i = IsDragging ? _dragIndex : SelectedIndex;
        if (i < 0 || i >= pts.Count) return;
        var p = pts[i];
        var text = Text($"{p.TemperatureC.ToString("F0", CultureInfo.CurrentCulture)}° · {p.DutyPercent.ToString("F0", CultureInfo.CurrentCulture)}%",
            LabelFontSize + 1, new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold), ChipText);
        var size = new Size(text.Width + 12, text.Height + 6);
        double x = Math.Clamp(positions[i].X - size.Width / 2, plot.Left + 2, plot.Right - size.Width - 2);
        double y = positions[i].Y - 16 - size.Height;
        if (y < plot.Top + 2) y = positions[i].Y + 16; // no room above: put it below the point
        var chip = new Rect(new Point(x, y), size);
        context.DrawRectangle(ChipBackground, new Pen(FocusBrush ?? DefaultFocusBrush, 1), chip, 6, 6);
        context.DrawText(text, new Point(chip.X + 6, chip.Y + 3));
    }

    private static FormattedText Text(string text, double size, Typeface typeface, IBrush brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
}
