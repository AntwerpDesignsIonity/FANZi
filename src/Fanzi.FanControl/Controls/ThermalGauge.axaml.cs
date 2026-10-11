using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Fanzi.FanControl.Converters;
using Fanzi.FanControl.Services;
using System;
using System.Globalization;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// Circular 270° gauge: a dark track, an animated value arc stroked with a conic temperature gradient,
/// a radial glow tinted by <see cref="TemperatureColorScale"/> and a centred gradient readout.
/// </summary>
public partial class ThermalGauge : UserControl
{
    /// <summary>Where the arc starts (degrees clockwise from 3 o'clock) — bottom-left.</summary>
    internal const double ArcStartAngle = 135;

    /// <summary>Total sweep of the dial in degrees.</summary>
    internal const double ArcSweep = 270;

    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<ThermalGauge, double?>(nameof(Value));

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ThermalGauge, double>(nameof(Minimum), 0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ThermalGauge, double>(nameof(Maximum), 110);

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<ThermalGauge, string?>(nameof(Label));

    public static readonly StyledProperty<string?> ValueTextProperty =
        AvaloniaProperty.Register<ThermalGauge, string?>(nameof(ValueText));

    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<ThermalGauge, string?>(nameof(Unit));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<ThermalGauge, double>(nameof(Size), 160);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<ThermalGauge, double>(nameof(Thickness), 12);

    /// <summary>When true the colour scale runs red (low) → blue (high), e.g. for a health score.</summary>
    public static readonly StyledProperty<bool> IsInvertedProperty =
        AvaloniaProperty.Register<ThermalGauge, bool>(nameof(IsInverted));

    private static readonly IBrush MutedValueBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x6A, 0x8F));
    private static readonly Color IdleGlowColor = Color.FromRgb(0x25, 0x63, 0xEB);

    // The XAML transitions, kept so "Reduce motion" can switch them off and back on.
    private readonly Transitions? _sweepTransitions;
    private readonly Transitions? _glowTransitions;

    /// <summary>Measured update rhythm of <see cref="Value"/> (sets the sweep duration).</summary>
    private readonly LiveCadence _cadence = new(TimeSpan.FromMilliseconds(600));

    /// <summary>Duration the arc's next sweep uses. Exposed for tests.</summary>
    internal TimeSpan SweepDuration => _sweepTransitions is { Count: > 0 } && _sweepTransitions[0] is DoubleTransition d ? d.Duration : TimeSpan.Zero;

    public ThermalGauge()
    {
        InitializeComponent();
        _sweepTransitions = PART_Value.Transitions;
        _glowTransitions = PART_Glow.Transitions;
        UpdateGeometry();
        UpdateArcBrush();
        UpdateValue();
        UpdateTexts();
    }

    public double? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? ValueText { get => GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool IsInverted { get => GetValue(IsInvertedProperty); set => SetValue(IsInvertedProperty, value); }

    /// <summary>Sweep in degrees for <paramref name="value"/> on [min, max]; null/NaN → 0, clamped to 0..270.</summary>
    internal static double ComputeSweep(double? value, double min, double max)
    {
        if (value is not double v || double.IsNaN(v) || !(max > min)) return 0;
        return ArcSweep * Math.Clamp((v - min) / (max - min), 0, 1);
    }

    /// <summary>Colour of the scale at <paramref name="value"/> for this gauge's range/orientation.</summary>
    internal static Color ColorAt(double value, double min, double max, bool inverted)
    {
        if (!inverted) return TemperatureColorScale.ForTemperature(value);
        double normalized = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
        return TemperatureColorScale.ForHealth(normalized * 100);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (PART_Root is null) return; // before InitializeComponent

        var p = change.Property;
        if (p == SizeProperty || p == ThicknessProperty)
        {
            UpdateGeometry();
            UpdateArcBrush();
        }
        else if (p == MinimumProperty || p == MaximumProperty || p == IsInvertedProperty)
        {
            UpdateArcBrush();
            UpdateValue();
        }
        else if (p == ValueProperty)
        {
            // v4.0.3: the arc sweeps linearly over ~92 % of the measured poll interval, so it tracks the
            // readings continuously instead of snapping (cubic ease-out) and resting for the rest of the second.
            if (_sweepTransitions is { Count: > 0 } && _sweepTransitions[0] is DoubleTransition sweep)
                sweep.Duration = _cadence.Next();
            UpdateValue();
            UpdateTexts();
        }
        else if (p == LabelProperty || p == ValueTextProperty || p == UnitProperty)
        {
            UpdateTexts();
        }
    }

    private void UpdateGeometry()
    {
        double size = Math.Max(24, Size);
        double thickness = Math.Clamp(Thickness, 1, size / 4);

        PART_Root.Width = size;
        PART_Root.Height = size;
        PART_Track.StrokeThickness = thickness;
        PART_Value.StrokeThickness = thickness;
        PART_Glow.Margin = new Thickness(-thickness * 1.5);
        PART_Aura.Margin = new Thickness(-thickness * 0.9);

        PART_ValueText.FontSize = Math.Max(10, size * 0.22);
        PART_UnitText.FontSize = Math.Max(7, size * 0.085);
        PART_UnitText.Margin = new Thickness(2, size * 0.04, 0, 0);
        PART_LabelText.FontSize = Math.Max(8, size * 0.06);
        PART_LabelText.MaxWidth = size - thickness * 4;
    }

    /// <summary>
    /// Builds the conic stroke so the colour at each point of the dial matches the scale at that value.
    /// The conic brush measures 0° from 12 o'clock; the arc starts at 135° from 3 o'clock, i.e. 225°.
    /// </summary>
    private void UpdateArcBrush()
    {
        double size = Math.Max(24, Size);
        double min = Minimum, max = Maximum;
        bool inverted = IsInverted;

        var brush = new ConicGradientBrush
        {
            Center = new RelativePoint(size / 2, size / 2, RelativeUnit.Absolute),
            Angle = ArcStartAngle + 90,
        };

        const int samples = 12;
        double arcFraction = ArcSweep / 360.0; // 0.75 of the circle is drawn
        for (int i = 0; i <= samples; i++)
        {
            double t = (double)i / samples;
            brush.GradientStops.Add(new GradientStop(ColorAt(min + t * (max - min), min, max, inverted), t * arcFraction));
        }

        // The undrawn bottom gap fades back to the start colour so the start cap isn't tinted by the end colour.
        var first = ColorAt(min, min, max, inverted);
        var last = ColorAt(max, min, max, inverted);
        brush.GradientStops.Add(new GradientStop(last, arcFraction + 0.03));
        brush.GradientStops.Add(new GradientStop(first, 0.95));
        brush.GradientStops.Add(new GradientStop(first, 1));

        PART_Value.Stroke = brush;
    }

    private void UpdateValue()
    {
        var value = Value is double v && !double.IsNaN(v) ? v : (double?)null;

        // Reduce motion: the arc and glow jump to the new reading instead of sweeping.
        bool animate = !MotionPreferences.ReduceMotion;
        PART_Value.Transitions = animate ? _sweepTransitions : null;
        PART_Glow.Transitions = animate ? _glowTransitions : null;

        PART_Value.SweepAngle = ComputeSweep(value, Minimum, Maximum);
        PART_Value.IsVisible = value.HasValue;

        var glowColor = value.HasValue ? ColorAt(value.Value, Minimum, Maximum, IsInverted) : IdleGlowColor;
        PART_Glow.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(TemperatureColorScale.WithAlpha(glowColor, 0.10), 0),
                new GradientStop(TemperatureColorScale.WithAlpha(glowColor, 0.14), 0.6),
                new GradientStop(TemperatureColorScale.WithAlpha(glowColor, 0.40), 0.77),
                new GradientStop(TemperatureColorScale.WithAlpha(glowColor, 0), 1),
            },
        };
        PART_Glow.Opacity = value.HasValue ? 1 : 0.35;
        UpdateAura(glowColor, animate);

        PART_ValueText.Foreground = value.HasValue
            ? TemperatureToGradientConverter.CreateGradient(glowColor)
            : MutedValueBrush;
    }

    private static readonly Color AuraViolet = Color.FromRgb(0x8B, 0x5C, 0xF6);
    private static readonly Color AuraBlue = Color.FromRgb(0x38, 0xBD, 0xF8);
    private Color? _auraColor;

    /// <summary>
    /// Living gradient behind the dial: a conic ring of the reading's colour flowing through brand violet and
    /// blue. It turns and breathes (class <c>living</c>) unless Reduce motion is on.
    /// </summary>
    private void UpdateAura(Color valueColor, bool animate)
    {
        PART_Aura.Classes.Set("living", animate);
        if (_auraColor == valueColor && PART_Aura.Fill is not null) return;
        _auraColor = valueColor;
        PART_Aura.Fill = BuildAuraBrush(valueColor);
    }

    /// <summary>Conic aura brush for <paramref name="valueColor"/> (seamless: first stop = last stop). Exposed for tests.</summary>
    internal static ConicGradientBrush BuildAuraBrush(Color valueColor) => new()
    {
        GradientStops =
        {
            new GradientStop(TemperatureColorScale.WithAlpha(valueColor, 0.85), 0),
            new GradientStop(TemperatureColorScale.WithAlpha(AuraViolet, 0.55), 0.25),
            new GradientStop(TemperatureColorScale.WithAlpha(valueColor, 0.20), 0.5),
            new GradientStop(TemperatureColorScale.WithAlpha(AuraBlue, 0.55), 0.75),
            new GradientStop(TemperatureColorScale.WithAlpha(valueColor, 0.85), 1),
        },
    };

    /// <summary>True while the aura ring is animating (Reduce motion off). Exposed for tests.</summary>
    internal bool IsAuraLiving => PART_Aura.Classes.Contains("living");

    private void UpdateTexts()
    {
        string valueText = !string.IsNullOrEmpty(ValueText)
            ? ValueText
            : Value is double v && !double.IsNaN(v) ? v.ToString("F0", CultureInfo.CurrentCulture) : "--";

        // Counts up/down to the new reading (instant with Reduce motion or from the "--" placeholder).
        AnimatedNumber.SetText(PART_ValueText, valueText);

        PART_UnitText.Text = Unit ?? string.Empty;
        PART_UnitText.IsVisible = !string.IsNullOrEmpty(Unit);
        PART_LabelText.Text = Label ?? string.Empty;
        PART_LabelText.IsVisible = !string.IsNullOrEmpty(Label);

        // Screen readers get the reading, not just "gauge": "CPU 72 °C", "CPU: no reading".
        AutomationProperties.SetName(this, AutomationNameFor(Label, valueText, Unit));
    }

    /// <summary>Accessible name of a gauge: label + reading + unit, or "label: no reading" for the placeholder.</summary>
    internal static string AutomationNameFor(string? label, string valueText, string? unit)
    {
        string name = string.IsNullOrWhiteSpace(label) ? "Gauge" : label.Trim();
        if (string.IsNullOrWhiteSpace(valueText) || valueText == "--") return name + ": no reading";
        string reading = string.IsNullOrEmpty(unit) ? valueText : unit.StartsWith('/') ? valueText + unit : valueText + " " + unit;
        return name + " " + reading;
    }
}
