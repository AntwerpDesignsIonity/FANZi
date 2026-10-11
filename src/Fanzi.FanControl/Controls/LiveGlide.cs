using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using System;
using System.Runtime.CompilerServices;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// v4.0.3 attached behaviour for live bars (<c>ProgressBar.thin</c>, <c>ProgressBar.heat-bar</c>): the fill glides
/// <b>linearly</b> from one real reading to the next over ~92 % of the measured update interval
/// (<see cref="LiveCadence"/>), so a bar moves continuously with the data instead of snapping (no transition) or
/// jump-then-stall (the old 0.6 s cubic ease-out on a 1 s poll).
/// <para>The bar gets its own local <see cref="Transitions"/> (overriding the theme's) holding one
/// <see cref="DoubleTransition"/> on <see cref="RangeBase.ValueProperty"/>. Its <see cref="Transition{T}.Duration"/> is
/// re-tuned after every reading for the next one — never swapped mid-glide, which would cancel the running
/// transition and make the bar jump. Bars without <see cref="HeatBar"/> also keep a 0.6 s foreground colour fade.
/// "Reduce motion" clears the transitions (bars jump straight to each reading).</para>
/// </summary>
public static class LiveGlide
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ProgressBar, bool>("IsEnabled", typeof(LiveGlide));

    /// <summary>Glide used for a bar's first change, before its cadence is known.</summary>
    public static readonly TimeSpan DefaultGlide = TimeSpan.FromMilliseconds(600);

    private static readonly TimeSpan ColourFade = TimeSpan.FromMilliseconds(600);

    private static readonly ConditionalWeakTable<ProgressBar, State> States = new();

    static LiveGlide()
    {
        IsEnabledProperty.Changed.AddClassHandler<ProgressBar>((bar, e) => OnEnabledChanged(bar, e.GetNewValue<bool>()));
        RangeBase.ValueProperty.Changed.AddClassHandler<ProgressBar>((bar, _) => OnValueChanged(bar));
    }

    public static bool GetIsEnabled(ProgressBar bar) => bar.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ProgressBar bar, bool value) => bar.SetValue(IsEnabledProperty, value);

    /// <summary>Duration the bar's next glide will use (null when the behaviour is off). Exposed for tests.</summary>
    internal static TimeSpan? NextGlide(ProgressBar bar)
        => States.TryGetValue(bar, out var s) ? s.Glide.Duration : null;

    /// <summary>The bar's reading (base value, not the mid-glide animated value).</summary>
    internal static double ReadingOf(ProgressBar bar)
    {
        var baseValue = bar.GetBaseValue(RangeBase.ValueProperty);
        return baseValue.HasValue ? baseValue.Value : bar.Value;
    }

    private sealed class State
    {
        public required DoubleTransition Glide;
        public required Transitions Transitions;
        public readonly LiveCadence Cadence = new(DefaultGlide);
        public double LastReading = double.NaN;
    }

    private static void OnEnabledChanged(ProgressBar bar, bool enabled)
    {
        if (!enabled)
        {
            if (States.TryGetValue(bar, out var old))
            {
                States.Remove(bar);
                if (ReferenceEquals(bar.Transitions, old.Transitions))
                    bar.ClearValue(Animatable.TransitionsProperty);
            }
            return;
        }

        if (States.TryGetValue(bar, out _)) return;

        var glide = new DoubleTransition
        {
            Property = RangeBase.ValueProperty,
            Duration = DefaultGlide,
            Easing = new LinearEasing(),
        };
        var transitions = new Transitions { glide };
        if (!HeatBar.GetIsEnabled(bar))
            transitions.Add(new BrushTransition { Property = TemplatedControl.ForegroundProperty, Duration = ColourFade });

        var state = new State { Glide = glide, Transitions = transitions, LastReading = ReadingOf(bar) };
        States.Add(bar, state);
        ApplyMotionPreference(bar, state);
    }

    private static void OnValueChanged(ProgressBar bar)
    {
        if (!States.TryGetValue(bar, out var state)) return;

        // Fires for every animated frame too; only a new base value is a new reading.
        double reading = ReadingOf(bar);
        if (reading.Equals(state.LastReading)) return;
        state.LastReading = reading;

        // This reading's glide has already started with the previous estimate; tune the next one.
        state.Glide.Duration = state.Cadence.Next();
        ApplyMotionPreference(bar, state);
    }

    /// <summary>Own transitions while motion is allowed; none with "Reduce motion" (re-checked on every reading).</summary>
    private static void ApplyMotionPreference(ProgressBar bar, State state)
    {
        if (MotionPreferences.ReduceMotion)
        {
            if (bar.Transitions is not null) bar.SetValue(Animatable.TransitionsProperty, null, BindingPriority.LocalValue);
        }
        else if (!ReferenceEquals(bar.Transitions, state.Transitions))
        {
            bar.Transitions = state.Transitions;
        }
    }
}
