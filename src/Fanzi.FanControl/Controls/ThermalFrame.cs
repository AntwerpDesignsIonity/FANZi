using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;
using System.Collections.Generic;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// Running temperature line around the whole window frame (FANZi 4.1).
/// <list type="bullet">
/// <item>The line's colour is the live system temperature on <see cref="ThermalFrameMath"/>'s scale:
/// blue = cold, green = stable, amber / orange = warm, red = hot. The shown value eases toward each reading.</item>
/// <item>Bands of the neighbouring scale colours flow around the frame, and two bright comets run clockwise —
/// slowly when cold (~20 s a lap), fast when hot (~4 s), with a pulse in the critical state.</item>
/// <item>Drawn by four thin edge strips (top / right / bottom / left) that clip to themselves, so each frame only
/// dirties the window border, never the dashboard behind it (see <c>UseRegionDirtyRectClipping</c> in Program).</item>
/// <item>Frame-synced via <see cref="TopLevel.RequestAnimationFrame"/>, capped at <see cref="MaxFps"/>; paused while
/// the window is minimised / hidden, and static with <see cref="IsAnimated"/> off or Reduce motion on.</item>
/// </list>
/// Not hit-testable: clicks and the OS resize border pass straight through.
/// </summary>
public sealed class ThermalFrame : Control
{
    /// <summary>Temperature (°C) the frame shows; null = no sensor (neutral navy line).</summary>
    public static readonly StyledProperty<double?> TemperatureProperty =
        AvaloniaProperty.Register<ThermalFrame, double?>(nameof(Temperature));

    /// <summary>Run the line (false = static gradient, redrawn only when the temperature changes).</summary>
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<ThermalFrame, bool>(nameof(IsAnimated), true);

    /// <summary>Outer corner radius of the frame (match the window corners; 0 when maximised).</summary>
    public static readonly StyledProperty<double> FrameRadiusProperty =
        AvaloniaProperty.Register<ThermalFrame, double>(nameof(FrameRadius), 8);

    /// <summary>Width of the bright core line in pixels.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<ThermalFrame, double>(nameof(StrokeThickness), 2);

    /// <summary>Inward glow width beyond the core line.</summary>
    public const double GlowWidth = 7;

    /// <summary>Frame-rate cap for the running line.</summary>
    public const double MaxFps = 50;

    /// <summary>Spacing (px) between gradient samples along a straight edge.</summary>
    private const double SampleSpacing = 8;

    public double? Temperature { get => GetValue(TemperatureProperty); set => SetValue(TemperatureProperty, value); }
    public bool IsAnimated { get => GetValue(IsAnimatedProperty); set => SetValue(IsAnimatedProperty, value); }
    public double FrameRadius { get => GetValue(FrameRadiusProperty); set => SetValue(FrameRadiusProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private readonly Edge[] _edges;
    private Size _size;
    private double _radius, _thickness;
    private double _perimeter;
    private readonly double[] _lineLength = new double[4];
    private readonly double[] _lineStart = new double[4]; // perimeter distance where each straight edge starts
    private readonly double[] _arcStart = new double[4];  // perimeter distance where the corner after edge i starts
    private double _arcLength;
    private readonly Matrix[] _lineTransform = new Matrix[4];
    private readonly Geometry?[,] _arcGeometry = new Geometry?[4, 3]; // [corner, core / glow1 / glow2]

    // Per-frame drawing data (built once, shared by the four edges)
    private readonly IBrush?[,] _lineBrush = new IBrush?[4, 3];
    private readonly IPen?[,] _arcPen = new IPen?[4, 3];

    private double _shown = double.NaN;
    private double _phase;
    private double _time;
    private bool _frameRequested;
    private TimeSpan? _lastTick;
    private TimeSpan _sinceDraw;
    private Window? _window;

    public ThermalFrame()
    {
        IsHitTestVisible = false;
        Focusable = false;
        _edges = new Edge[4];
        for (int i = 0; i < 4; i++)
        {
            _edges[i] = new Edge(this);
            VisualChildren.Add(_edges[i]);
        }
    }

    // ── Test / diagnostics surface ───────────────────────────────────────────

    /// <summary>Temperature currently drawn (eases toward <see cref="Temperature"/>); NaN when no sensor.</summary>
    internal double ShownTemperature => _shown;

    /// <summary>Laps travelled by the running comets (fractional part = head position).</summary>
    internal double Phase => _phase;

    /// <summary>True while the animation loop is scheduled and allowed to run.</summary>
    internal bool IsRunning => _frameRequested && ShouldAnimate;

    /// <summary>Base (un-highlighted) colour of the shown temperature.</summary>
    internal Color CurrentColor => ThermalFrameMath.ColorFor(double.IsNaN(_shown) ? null : _shown);

    /// <summary>Thermal state of the shown temperature.</summary>
    internal ThermalState State => ThermalFrameMath.StateFor(double.IsNaN(_shown) ? null : _shown);

    /// <summary>The four edge strips (top, right, bottom, left).</summary>
    internal IReadOnlyList<Control> Edges => _edges;

    /// <summary>Advances the animation by <paramref name="seconds"/> and redraws (used by the frame loop and tests).</summary>
    internal void Advance(double seconds)
    {
        double? target = Temperature;
        if (target is double t && !double.IsNaN(t))
            _shown = ThermalFrameMath.Ease(_shown, t, seconds);
        else
            _shown = double.NaN;

        double? shown = double.IsNaN(_shown) ? null : _shown;
        _phase += Math.Max(0, seconds) * ThermalFrameMath.LapsPerSecond(shown);
        _phase -= Math.Floor(_phase / 1000) * 1000; // keep precision over very long sessions
        _time += Math.Max(0, seconds);
        BuildFrame();
        foreach (var e in _edges) e.InvalidateVisual();
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var e in _edges) e.Measure(availableSize);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _size = finalSize;
        double w = finalSize.Width, h = finalSize.Height;
        _thickness = Math.Max(0.5, StrokeThickness);
        _radius = Math.Clamp(FrameRadius, 0, Math.Min(w, h) / 2);
        double band = Math.Min(Math.Max(_radius, _thickness + GlowWidth) + 1, Math.Min(w, h) / 2);
        band = Math.Max(0, band);

        _edges[0].Arrange(new Rect(0, 0, w, band));                                   // top
        _edges[1].Arrange(new Rect(Math.Max(0, w - band), band, band, Math.Max(0, h - 2 * band))); // right
        _edges[2].Arrange(new Rect(0, Math.Max(0, h - band), w, band));               // bottom
        _edges[3].Arrange(new Rect(0, band, band, Math.Max(0, h - 2 * band)));          // left

        BuildGeometry();
        BuildFrame();
        RequestTick(); // first real size (or a restore from a zero size) starts the loop
        return finalSize;
    }

    private void BuildGeometry()
    {
        double w = _size.Width, h = _size.Height, r = _radius, t = _thickness;
        _lineLength[0] = _lineLength[2] = Math.Max(0, w - 2 * r);
        _lineLength[1] = _lineLength[3] = Math.Max(0, h - 2 * r);
        bool arcs = r > t + 0.5;
        _arcLength = arcs ? Math.PI / 2 * (r - t / 2) : 0;

        double s = 0;
        for (int i = 0; i < 4; i++)
        {
            _lineStart[i] = s;
            s += _lineLength[i];
            _arcStart[i] = s;
            s += _arcLength;
        }
        _perimeter = s;

        // Each straight edge is drawn in a local frame: +x = clockwise travel, +y = inward, y = 0 on the window edge.
        _lineTransform[0] = Matrix.CreateTranslation(r, 0);
        _lineTransform[1] = Matrix.CreateRotation(Math.PI / 2) * Matrix.CreateTranslation(w, r);
        _lineTransform[2] = Matrix.CreateRotation(Math.PI) * Matrix.CreateTranslation(w - r, h);
        _lineTransform[3] = Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(0, h - r);

        // Corners after each edge: TR, BR, BL, TL (clockwise quarter arcs).
        var centres = new[] { new Point(w - r, r), new Point(w - r, h - r), new Point(r, h - r), new Point(r, r) };
        double[] widths = { t, t + 3, t + GlowWidth };
        for (int c = 0; c < 4; c++)
        {
            for (int k = 0; k < 3; k++)
            {
                double radius = r - widths[k] / 2;
                _arcGeometry[c, k] = arcs && radius > 0.5 ? QuarterArc(centres[c], radius, -90 + 90 * c) : null;
            }
        }
    }

    private static Geometry QuarterArc(Point centre, double radius, double startDeg)
    {
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + 90) * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(centre.X + radius * Math.Cos(a0), centre.Y + radius * Math.Sin(a0)), false);
            ctx.ArcTo(new Point(centre.X + radius * Math.Cos(a1), centre.Y + radius * Math.Sin(a1)),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        return geometry;
    }

    // ── Per-frame drawing data ──────────────────────────────────────────────

    private void BuildFrame()
    {
        if (_perimeter <= 0) return;
        double? temp = double.IsNaN(_shown) ? null : _shown;
        var stops = new List<ImmutableGradientStop>(256);

        for (int i = 0; i < 4; i++)
        {
            double len = _lineLength[i];
            if (len <= 0)
            {
                _lineBrush[i, 0] = _lineBrush[i, 1] = _lineBrush[i, 2] = null;
                continue;
            }

            stops.Clear();
            int n = Math.Max(2, (int)Math.Ceiling(len / SampleSpacing) + 1);
            for (int k = 0; k < n; k++)
            {
                double off = (double)k / (n - 1);
                stops.Add(new ImmutableGradientStop(off, ColorAtDistance(_lineStart[i] + off * len, temp)));
            }
            // Crisp comet heads: an exact stop on each head and the plain line just ahead of it.
            for (int c = 0; c < ThermalFrameMath.CometCount; c++)
            {
                double d = ThermalFrameMath.HeadPosition(_phase, c) * _perimeter - _lineStart[i];
                if (d <= 0 || d >= len - 0.5) continue;
                double off = d / len, ahead = Math.Min(1, off + 0.6 / len);
                stops.Add(new ImmutableGradientStop(off, ColorAtDistance(_lineStart[i] + d - 0.01, temp)));
                stops.Add(new ImmutableGradientStop(ahead, ColorAtDistance(_lineStart[i] + d + 0.6, temp)));
            }
            stops.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var array = stops.ToArray();
            var start = new RelativePoint(0, 0.5, RelativeUnit.Relative);
            var end = new RelativePoint(1, 0.5, RelativeUnit.Relative);
            _lineBrush[i, 0] = new ImmutableLinearGradientBrush(array, 1, startPoint: start, endPoint: end);
            _lineBrush[i, 1] = new ImmutableLinearGradientBrush(array, 0.30, startPoint: start, endPoint: end);
            _lineBrush[i, 2] = new ImmutableLinearGradientBrush(array, 0.13, startPoint: start, endPoint: end);
        }

        double[] widths = { _thickness, _thickness + 3, _thickness + GlowWidth };
        double[] opacity = { 1, 0.30, 0.13 };
        for (int c = 0; c < 4; c++)
        {
            var color = ColorAtDistance(_arcStart[c] + _arcLength / 2, temp);
            for (int k = 0; k < 3; k++)
                _arcPen[c, k] = _arcGeometry[c, k] is null ? null
                    : new ImmutablePen(new ImmutableSolidColorBrush(color, opacity[k]), widths[k]);
        }
    }

    private Color ColorAtDistance(double distance, double? temp)
        => ThermalFrameMath.Sample(ThermalFrameMath.Frac(distance / _perimeter), temp, _phase, _time);

    /// <summary>Draws the whole frame clipped to one edge strip (offset = the strip's position in the frame).</summary>
    private void RenderEdge(DrawingContext context, Rect edgeBounds)
    {
        if (_perimeter <= 0 || edgeBounds.Width <= 0 || edgeBounds.Height <= 0) return;
        using (context.PushClip(new Rect(edgeBounds.Size)))
        using (context.PushTransform(Matrix.CreateTranslation(-edgeBounds.X, -edgeBounds.Y)))
        {
            for (int i = 0; i < 4; i++)
            {
                if (_lineBrush[i, 0] is null) continue;
                using (context.PushTransform(_lineTransform[i]))
                {
                    double len = _lineLength[i];
                    context.DrawRectangle(_lineBrush[i, 2], null, new Rect(0, 0, len, _thickness + GlowWidth));
                    context.DrawRectangle(_lineBrush[i, 1], null, new Rect(0, 0, len, _thickness + 3));
                    context.DrawRectangle(_lineBrush[i, 0], null, new Rect(0, 0, len, _thickness));
                }
            }
            for (int c = 0; c < 4; c++)
            {
                for (int k = 2; k >= 0; k--)
                {
                    if (_arcGeometry[c, k] is { } g && _arcPen[c, k] is { } pen)
                        context.DrawGeometry(null, pen, g);
                }
            }
        }
    }

    // ── Animation loop ──────────────────────────────────────────────────────

    private bool ShouldAnimate =>
        IsAnimated && !MotionPreferences.ReduceMotion && IsVisible && _size.Width > 0 && _size.Height > 0
        && TopLevel.GetTopLevel(this) is { } top && top.IsVisible
        && _window?.WindowState != WindowState.Minimized;

    private void RequestTick()
    {
        if (_frameRequested || !ShouldAnimate) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        top.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan now)
    {
        _frameRequested = false;
        if (!ShouldAnimate)
        {
            _lastTick = null;
            return;
        }

        if (_lastTick is { } last)
        {
            var dt = now - last;
            if (dt < TimeSpan.Zero) dt = TimeSpan.Zero;
            _sinceDraw += dt;
        }
        _lastTick = now;

        if (_sinceDraw.TotalSeconds >= 1.0 / MaxFps)
        {
            Advance(Math.Min(_sinceDraw.TotalSeconds, 0.25));
            _sinceDraw = TimeSpan.Zero;
        }
        RequestTick();
    }

    /// <summary>Static redraw (no motion) or (re)start of the loop after a state change.</summary>
    private void Refresh()
    {
        if (ShouldAnimate)
        {
            RequestTick();
        }
        else
        {
            _lastTick = null;
            _sinceDraw = TimeSpan.Zero;
            double? target = Temperature;
            _shown = target is double t && !double.IsNaN(t) ? t : double.NaN;
            BuildFrame();
            foreach (var e in _edges) e.InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TemperatureProperty)
        {
            // The very first reading snaps (no sweep up from nothing); later readings ease in the loop.
            if (double.IsNaN(_shown) && Temperature is double t && !double.IsNaN(t))
            {
                _shown = t;
                BuildFrame();
                foreach (var e in _edges) e.InvalidateVisual();
            }
            Refresh();
        }
        else if (change.Property == IsAnimatedProperty || change.Property == IsVisibleProperty)
        {
            Refresh();
        }
        else if (change.Property == FrameRadiusProperty || change.Property == StrokeThicknessProperty)
        {
            InvalidateArrange();
            foreach (var e in _edges) e.InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += OnWindowPropertyChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        _lastTick = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty)
        {
            _lastTick = null; // no catch-up jump after a restore
            _sinceDraw = TimeSpan.Zero;
            Refresh();
        }
    }

    /// <summary>One thin strip of the frame; renders the shared drawing clipped to itself.</summary>
    private sealed class Edge : Control
    {
        private readonly ThermalFrame _owner;

        public Edge(ThermalFrame owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext context) => _owner.RenderEdge(context, Bounds);
    }
}
