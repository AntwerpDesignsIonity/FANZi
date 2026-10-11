using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// Rolling temperature / load chart for a <see cref="MetricHistory"/>: a smooth monotone curve stroked with a
/// vertical heat gradient (<see cref="TemperatureColorScale.ForHeat(double, double, double)"/>: LOW
/// <see cref="HeatLow"/> = blue → HOT <see cref="HeatHigh"/> = red, colour follows the value on the Y axis), a
/// gradient area fill fading to transparent, faint grid lines, min / max / now labels and a pulsing
/// "now" dot. The window's highest sample gets an orange peak marker (dot, dashed guide, "▲ value" label);
/// when the live value sets a NEW peak the now-dot turns orange and the header shows "▲ NEW PEAK".
/// Drawn directly in <see cref="Render"/>; the dot is two child ellipses so the theme can
/// animate them (<c>Ellipse.history-now-glow</c>, plus <c>.new-peak</c>).
/// <para>The history object is mutated in place, so bind <see cref="Revision"/> to a counter that
/// changes after every recorded poll.</para>
/// </summary>
public sealed class ThermalHistoryChart : Control
{
    public static readonly StyledProperty<MetricHistory?> HistoryProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, MetricHistory?>(nameof(History));

    public static readonly StyledProperty<TimeSpan> TimeWindowProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, TimeSpan>(nameof(TimeWindow), TimeSpan.FromMinutes(5));

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, int>(nameof(Revision));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, string?>(nameof(Title));

    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, string>(nameof(Unit), "°");

    /// <summary>False = sparkline: no title, stats, grid or axis labels — just the curve, fill and dot.</summary>
    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, bool>(nameof(ShowLabels), true);

    /// <summary>Smallest Y range shown (°C) so a flat line doesn't turn noise into cliffs.</summary>
    public static readonly StyledProperty<double> MinimumSpanProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, double>(nameof(MinimumSpan), 10);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, double>(nameof(StrokeThickness), 2);

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, IBrush?>(nameof(LabelBrush));

    public static readonly StyledProperty<IBrush?> TitleBrushProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, IBrush?>(nameof(TitleBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, IBrush?>(nameof(GridBrush));

    /// <summary>Value drawn LOW blue (0 % of the heat scale). Default 30 °C; use 0 for a % load chart.</summary>
    public static readonly StyledProperty<double> HeatLowProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, double>(nameof(HeatLow), TemperatureColorScale.MinStop);

    /// <summary>Value drawn HOT red (100 % of the heat scale). Default 95 °C; use 100 for a % load chart.</summary>
    public static readonly StyledProperty<double> HeatHighProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, double>(nameof(HeatHigh), TemperatureColorScale.MaxStop);

    /// <summary>Show the orange peak marker (dot, guide line and label) for the window's highest sample.</summary>
    public static readonly StyledProperty<bool> ShowPeakProperty =
        AvaloniaProperty.Register<ThermalHistoryChart, bool>(nameof(ShowPeak), true);

    // Fallbacks when the theme style isn't applied (FanziTextMuted / TextPrimary / Border).
    private static readonly IBrush DefaultLabelBrush = new SolidColorBrush(Color.Parse("#6A8AAF"));
    private static readonly IBrush DefaultTitleBrush = new SolidColorBrush(Color.Parse("#E8F0FF"));
    private static readonly IBrush DefaultGridBrush = new SolidColorBrush(Color.Parse("#1A2845"));

    private const double LabelFontSize = 10;
    private const double StatsFontSize = 11;
    private const double TitleFontSize = 12;
    private const int GradientSamples = 10;

    private readonly Ellipse _glow;
    private readonly Ellipse _dot;
    private Layout? _layout;
    private Color _dotColor;

    static ThermalHistoryChart()
    {
        AffectsArrange<ThermalHistoryChart>(HistoryProperty, TimeWindowProperty, RevisionProperty,
            ShowLabelsProperty, MinimumSpanProperty, HeatLowProperty, HeatHighProperty, ShowPeakProperty);
        AffectsRender<ThermalHistoryChart>(HistoryProperty, TimeWindowProperty, RevisionProperty, TitleProperty,
            UnitProperty, ShowLabelsProperty, MinimumSpanProperty, StrokeThicknessProperty,
            LabelBrushProperty, TitleBrushProperty, GridBrushProperty, HeatLowProperty, HeatHighProperty, ShowPeakProperty);
    }

    public ThermalHistoryChart()
    {
        ClipToBounds = false;
        _glow = new Ellipse { IsHitTestVisible = false };
        _glow.Classes.Add("history-now-glow");
        _dot = new Ellipse { IsHitTestVisible = false, StrokeThickness = 1.5, Stroke = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) };
        _dot.Classes.Add("history-now-dot");
        VisualChildren.Add(_glow);
        VisualChildren.Add(_dot);
        LogicalChildren.Add(_glow);
        LogicalChildren.Add(_dot);
    }

    public MetricHistory? History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public TimeSpan TimeWindow { get => GetValue(TimeWindowProperty); set => SetValue(TimeWindowProperty, value); }
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }
    public double MinimumSpan { get => GetValue(MinimumSpanProperty); set => SetValue(MinimumSpanProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? TitleBrush { get => GetValue(TitleBrushProperty); set => SetValue(TitleBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public double HeatLow { get => GetValue(HeatLowProperty); set => SetValue(HeatLowProperty, value); }
    public double HeatHigh { get => GetValue(HeatHighProperty); set => SetValue(HeatHighProperty, value); }
    public bool ShowPeak { get => GetValue(ShowPeakProperty); set => SetValue(ShowPeakProperty, value); }

    /// <summary>Position of the "now" dot in local coordinates (null until two samples exist). Exposed for tests.</summary>
    internal Point? NowPoint => _layout?.Points is { Length: > 0 } p ? p[^1] : null;

    /// <summary>Min / max / now of the visible window (null when empty). Exposed for tests.</summary>
    internal SeriesStats? Stats => _layout?.Stats;

    /// <summary>Highest sample of the visible window and whether the live value just set it. Exposed for tests.</summary>
    internal SeriesPeak? Peak => _layout?.Peak;

    /// <summary>Where the orange peak marker is drawn (null when there is no peak). Exposed for tests.</summary>
    internal Point? PeakPoint => _layout?.PeakPoint;

    /// <summary>Fill colour of the "now" dot: orange on a NEW peak, otherwise the heat colour of the live value.</summary>
    internal Color NowDotColor => _dotColor;

    /// <summary>Heat-scale colour (LOW blue → HOT red) of <paramref name="value"/> for this chart's range.</summary>
    internal Color HeatColor(double value) => TemperatureColorScale.ForHeat(value, HeatLow, HeatHigh);

    // ── Layout ────────────────────────────────────────────────────────────────

    private sealed record Layout(Rect Plot, AxisRange Axis, Point[] Points, SeriesStats? Stats, DateTime Start, DateTime End,
        SeriesPeak? Peak, Point? PeakPoint);

    /// <summary>Plot area inside <paramref name="size"/>: room for the header row and axis labels when shown.</summary>
    internal static Rect PlotRect(Size size, bool showLabels)
    {
        var pad = showLabels ? new Thickness(32, 24, 14, 18) : new Thickness(2, 4, 8, 3);
        return new Rect(pad.Left, pad.Top,
            Math.Max(0, size.Width - pad.Left - pad.Right),
            Math.Max(0, size.Height - pad.Top - pad.Bottom));
    }

    private Layout ComputeLayout(Size size)
    {
        var plot = PlotRect(size, ShowLabels);
        var window = History?.GetWindow(TimeWindow) ?? Array.Empty<HistorySample>();
        var stats = SeriesMath.ComputeStats(window);
        var axis = stats is { } s
            ? SeriesMath.ComputeAxis(s.Min, s.Max, MinimumSpan)
            : SeriesMath.ComputeAxis(40, 80, MinimumSpan);

        DateTime end = window.Count > 0 ? window[^1].TimeUtc : DateTime.UtcNow;
        DateTime start = end - TimeWindow;

        var points = window.Count >= 2
            ? SeriesMath.Project(SeriesMath.Downsample(window, (int)(plot.Width / 2)), start, end, plot, axis)
            : Array.Empty<Point>();

        // Peak from the raw window (not the downsampled curve) so the true maximum is marked.
        var peak = ShowPeak && points.Length >= 2 ? SeriesMath.ComputePeak(window) : null;
        Point? peakPoint = peak is { } pk
            ? SeriesMath.Project(new[] { new HistorySample(pk.TimeUtc, pk.Value) }, start, end, plot, axis)[0]
            : null;
        return new Layout(plot, axis, points, stats, start, end, peak, peakPoint);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _glow.Measure(Size.Infinity);
        _dot.Measure(Size.Infinity);
        return new Size(0, ShowLabels ? 140 : 24);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layout = ComputeLayout(finalSize);

        double glowSize = ShowLabels ? 18 : 12;
        double dotSize = ShowLabels ? 7 : 5;
        if (NowPoint is { } now && _layout.Stats is { } stats)
        {
            // NEW peak → the live dot turns orange; otherwise it carries the heat colour of the live value.
            bool newPeak = _layout.Peak is { IsNew: true };
            var color = newPeak ? TemperatureColorScale.PeakColor : HeatColor(stats.Current);
            _dot.Classes.Set("new-peak", newPeak);
            _glow.Classes.Set("new-peak", newPeak);
            if (newPeak) { glowSize *= 1.35; dotSize += 1; }
            if (color != _dotColor || _dot.Fill is null)
            {
                _dotColor = color;
                _dot.Fill = new SolidColorBrush(color);
                _glow.Fill = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(TemperatureColorScale.WithAlpha(color, 0.85), 0),
                        new GradientStop(TemperatureColorScale.WithAlpha(color, 0.35), 0.45),
                        new GradientStop(TemperatureColorScale.WithAlpha(color, 0), 1),
                    },
                };
            }
            _glow.Arrange(new Rect(now.X - glowSize / 2, now.Y - glowSize / 2, glowSize, glowSize));
            _dot.Arrange(new Rect(now.X - dotSize / 2, now.Y - dotSize / 2, dotSize, dotSize));
        }
        else
        {
            _glow.Arrange(default);
            _dot.Arrange(default);
        }
        return finalSize;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var layout = _layout ?? ComputeLayout(Bounds.Size);
        var plot = layout.Plot;
        if (plot.Width < 4 || plot.Height < 4) return;

        var typeface = new Typeface(TextElement.GetFontFamily(this));
        var labelBrush = LabelBrush ?? DefaultLabelBrush;

        if (ShowLabels)
            DrawGridAndAxes(context, layout, typeface, labelBrush);

        if (layout.Points.Length >= 2)
            DrawSeries(context, layout);
        else if (ShowLabels)
            DrawCentred(context, "Collecting samples…", plot, typeface, labelBrush);

        if (ShowLabels)
            DrawHeader(context, layout, typeface, labelBrush);
    }

    private void DrawGridAndAxes(DrawingContext context, Layout layout, Typeface typeface, IBrush labelBrush)
    {
        var plot = layout.Plot;
        var axis = layout.Axis;
        var gridPen = new Pen(GridBrush ?? DefaultGridBrush, 1);
        var faintPen = new Pen(GridBrush ?? DefaultGridBrush, 1, new DashStyle(new double[] { 2, 4 }, 0));

        // Horizontal grid + value labels on the left.
        for (double v = axis.Min; v <= axis.Max + 1e-9; v += axis.Step)
        {
            double y = Math.Round(plot.Bottom - (v - axis.Min) / (axis.Max - axis.Min) * plot.Height) + 0.5;
            context.DrawLine(v == axis.Min ? gridPen : faintPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = Text(v.ToString("F0", CultureInfo.CurrentCulture) + Unit, LabelFontSize, typeface, labelBrush);
            context.DrawText(text, new Point(plot.Left - 6 - text.Width, y - text.Height / 2));
        }

        // Vertical grid every 15 s / 1 min / 5 min back from "now".
        var window = TimeWindow;
        var tick = window <= TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(15)
            : window <= TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(1)
            : TimeSpan.FromMinutes(5);
        for (var t = tick; t < window; t += tick)
        {
            double x = Math.Round(plot.Right - t / window * plot.Width) + 0.5;
            context.DrawLine(faintPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        // Time labels: window start on the left, "now" on the right.
        string startLabel = window < TimeSpan.FromMinutes(1)
            ? $"−{window.TotalSeconds:F0} s"
            : $"−{window.TotalMinutes:F0} min";
        var startText = Text(startLabel, LabelFontSize, typeface, labelBrush);
        var nowText = Text("now", LabelFontSize, typeface, labelBrush);
        double labelY = plot.Bottom + 3;
        context.DrawText(startText, new Point(plot.Left, labelY));
        context.DrawText(nowText, new Point(plot.Right - nowText.Width, labelY));
    }

    private void DrawSeries(DrawingContext context, Layout layout)
    {
        var plot = layout.Plot;
        var points = layout.Points;
        var controls = SeriesMath.MonotoneControlPoints(points);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], isFilled: false);
            for (int i = 0; i < controls.Length; i++)
                g.CubicBezierTo(controls[i].C1, controls[i].C2, points[i + 1]);
            g.EndFigure(isClosed: false);
        }

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(points[0].X, plot.Bottom), isFilled: true);
            g.LineTo(points[0]);
            for (int i = 0; i < controls.Length; i++)
                g.CubicBezierTo(controls[i].C1, controls[i].C2, points[i + 1]);
            g.LineTo(new Point(points[^1].X, plot.Bottom));
            g.EndFigure(isClosed: true);
        }

        // Area: colour by value, fading from ~40 % at the curve's highest point to 0 at the baseline.
        double top = plot.Bottom;
        foreach (var p in points) top = Math.Min(top, p.Y);
        context.DrawGeometry(VerticalScaleBrush(top, plot.Bottom, layout, t => 0.42 * (1 - t)), null, area);

        // Line: soft wide glow underneath, crisp gradient stroke on top.
        double thickness = Math.Max(1, StrokeThickness);
        var glowPen = new Pen(VerticalScaleBrush(plot.Top, plot.Bottom, layout, _ => 0.22), thickness * 3.5,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var linePen = new Pen(VerticalScaleBrush(plot.Top, plot.Bottom, layout, _ => 1), thickness,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, glowPen, line);
        context.DrawGeometry(null, linePen, line);

        if (layout.PeakPoint is { } peakAt && layout.Peak is { } peak)
            DrawPeakMarker(context, layout, peakAt, peak);
    }

    private static readonly IBrush PeakBrush = new SolidColorBrush(TemperatureColorScale.PeakColor);
    private static readonly IPen PeakRingPen = new Pen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)), 1.25);

    /// <summary>
    /// Orange peak marker: a faint dashed guide line at the peak level, a glowing orange dot on the highest
    /// sample and (full chart only) a "▲ 78°" label. Skipped on the dot itself when the live value IS the
    /// new peak — the orange live dot already says so.
    /// </summary>
    private void DrawPeakMarker(DrawingContext context, Layout layout, Point at, SeriesPeak peak)
    {
        var plot = layout.Plot;
        var orange = TemperatureColorScale.PeakColor;

        if (ShowLabels)
        {
            var guide = new Pen(new SolidColorBrush(TemperatureColorScale.WithAlpha(orange, 0.45)), 1,
                new DashStyle(new double[] { 3, 3 }, 0));
            double y = Math.Round(at.Y) + 0.5;
            context.DrawLine(guide, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        if (!peak.IsNew)
        {
            double r = ShowLabels ? 3.5 : 2.5;
            context.DrawEllipse(new SolidColorBrush(TemperatureColorScale.WithAlpha(orange, 0.30)), null, at, r * 2.2, r * 2.2);
            context.DrawEllipse(PeakBrush, PeakRingPen, at, r, r);
        }

        if (!ShowLabels) return;

        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Bold);
        var label = Text("▲ " + Format(peak.Value), LabelFontSize, typeface, PeakBrush);
        double lx = Math.Clamp(at.X - label.Width / 2, plot.Left, Math.Max(plot.Left, plot.Right - label.Width));
        double ly = at.Y - label.Height - 5;
        if (ly < plot.Top - 2) ly = at.Y + 6; // no room above: put it under the marker
        var pill = new Rect(lx - 3, ly - 1, label.Width + 6, label.Height + 2);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x12, 0x20)),
            new Pen(new SolidColorBrush(TemperatureColorScale.WithAlpha(orange, 0.6)), 1), pill, 4, 4);
        context.DrawText(label, new Point(lx, ly));
    }

    /// <summary>
    /// Vertical gradient between rows <paramref name="top"/> and <paramref name="bottom"/>: each stop takes the
    /// heat colour (LOW blue → HOT red) of the value drawn at that row, with alpha from
    /// <paramref name="alphaAt"/>(0 = top … 1 = bottom).
    /// </summary>
    private LinearGradientBrush VerticalScaleBrush(double top, double bottom, Layout layout, Func<double, double> alphaAt)
    {
        if (bottom - top < 1) top = bottom - 1;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, top, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, bottom, RelativeUnit.Absolute),
        };
        for (int i = 0; i <= GradientSamples; i++)
        {
            double t = (double)i / GradientSamples;
            double value = SeriesMath.ValueAtY(top + t * (bottom - top), layout.Plot, layout.Axis);
            brush.GradientStops.Add(new GradientStop(
                TemperatureColorScale.WithAlpha(HeatColor(value), alphaAt(t)), t));
        }
        return brush;
    }

    /// <summary>Title on the left; "min · max · now" on the right, the live value in its scale colour.</summary>
    private void DrawHeader(DrawingContext context, Layout layout, Typeface typeface, IBrush labelBrush)
    {
        double y = 0;
        if (!string.IsNullOrEmpty(Title))
        {
            var title = Text(Title!, TitleFontSize, new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.Black),
                TitleBrush ?? DefaultTitleBrush);
            context.DrawText(title, new Point(0, y));
        }

        if (layout.Stats is not { } stats) return;

        var boldTypeface = new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.Bold);
        bool newPeak = layout.Peak is { IsNew: true };
        var nowBrush = newPeak ? PeakBrush : new SolidColorBrush(HeatColor(stats.Current));
        var segments = new List<FormattedText>();
        if (newPeak)
            segments.Add(Text("▲ NEW PEAK   ", LabelFontSize, boldTypeface, PeakBrush));
        segments.Add(Text("min ", StatsFontSize, typeface, labelBrush));
        segments.Add(Text(Format(stats.Min), StatsFontSize, boldTypeface, new SolidColorBrush(HeatColor(stats.Min))));
        segments.Add(Text("   max ", StatsFontSize, typeface, labelBrush));
        segments.Add(Text(Format(stats.Max), StatsFontSize, boldTypeface,
            layout.Peak is not null ? PeakBrush : TitleBrush ?? DefaultTitleBrush));
        segments.Add(Text("   now ", StatsFontSize, typeface, labelBrush));
        segments.Add(Text(Format(stats.Current), StatsFontSize, boldTypeface, nowBrush));

        double x = Bounds.Width;
        foreach (var s in segments) x -= s.WidthIncludingTrailingWhitespace;
        foreach (var s in segments)
        {
            context.DrawText(s, new Point(x, y + 1));
            x += s.WidthIncludingTrailingWhitespace;
        }
    }

    private string Format(double value) => value.ToString("F0", CultureInfo.CurrentCulture) + Unit;

    private static void DrawCentred(DrawingContext context, string message, Rect area, Typeface typeface, IBrush brush)
    {
        var text = Text(message, StatsFontSize, typeface, brush);
        context.DrawText(text, new Point(area.Center.X - text.Width / 2, area.Center.Y - text.Height / 2));
    }

    private static FormattedText Text(string text, double size, Typeface typeface, IBrush brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
}
