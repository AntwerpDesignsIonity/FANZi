using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// Attached behaviour for big metric values: bind <c>controls:AnimatedNumber.Text</c> instead of
/// <c>TextBlock.Text</c> and a change from one reading to the next counts up/down over
/// <see cref="Duration"/> with a cubic ease-out, printed in the target's own format ("72,4 °C").
/// <para>The count runs on Avalonia's animation clock (the same one as the gauge sweeps), so it only
/// advances while the window renders. The text is set at once (no animation) when
/// <see cref="MotionPreferences.ReduceMotion"/> is on, when the block is not on screen, or when either text
/// is not a single number with the same unit (e.g. "--" → "72 °C").</para>
/// </summary>
public static class AnimatedNumber
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(400);

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(AnimatedNumber));

    /// <summary>Linear 0 → 1 driven by the animation; the cubic ease is applied when the value is printed.</summary>
    private static readonly AttachedProperty<double> ProgressProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, double>("Progress", typeof(AnimatedNumber));

    private static readonly ConditionalWeakTable<TextBlock, State> States = new();

    static AnimatedNumber()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, e) => OnTextChanged(block, e.GetNewValue<string?>()));
        ProgressProperty.Changed.AddClassHandler<TextBlock>((block, e) => OnProgress(block, e.GetNewValue<double>()));
    }

    public static string? GetText(TextBlock element) => element.GetValue(TextProperty);

    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>True while a count is in progress (tests, render harness).</summary>
    internal static bool IsAnimating(TextBlock block) => States.TryGetValue(block, out var state) && state.Active is not null;

    private sealed class State
    {
        public Tween? Active;
        public CancellationTokenSource? Cancel;
        public bool Hooked;
    }

    private sealed record Tween(double From, NumberText To, string TargetText);

    private static void OnTextChanged(TextBlock block, string? target)
    {
        var state = States.GetValue(block, _ => new State());
        state.Cancel?.Cancel();
        state.Cancel = null;

        NumberText from = default, to = default;
        bool animate = !MotionPreferences.ReduceMotion
                       && TopLevel.GetTopLevel(block) is not null
                       && block.IsEffectivelyVisible
                       && NumberTween.TryParse(target, out to)
                       && NumberTween.TryParse(block.Text, out from) // the shown text — mid-count when interrupted
                       && from.IsCompatibleWith(to)
                       && from.Value != to.Value;

        if (!animate)
        {
            state.Active = null;
            block.Text = target;
            return;
        }

        if (!state.Hooked)
        {
            // Leaving the visual tree (tab switch, window closed) stops the clock: land on the target.
            block.DetachedFromVisualTree += (_, _) => Finish(block);
            state.Hooked = true;
        }

        var tween = new Tween(from.Value, to, target!);
        state.Active = tween;
        state.Cancel = new CancellationTokenSource();
        _ = RunAsync(block, state, tween, state.Cancel.Token);
    }

    private static async Task RunAsync(TextBlock block, State state, Tween tween, CancellationToken token)
    {
        var animation = new Animation
        {
            Duration = Duration,
            Easing = new LinearEasing(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ProgressProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ProgressProperty, 1d) } },
            },
        };

        try
        {
            await animation.RunAsync(block, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Still the current count (not superseded by a newer reading): show the exact target text.
            if (ReferenceEquals(state.Active, tween)) Finish(block);
        }
    }

    private static void OnProgress(TextBlock block, double progress)
    {
        // 0 is the start (already shown) and the base value the property falls back to when a run ends or is cancelled.
        if (progress <= 0 || !States.TryGetValue(block, out var state) || state.Active is not { } tween) return;

        if (progress >= 1 || MotionPreferences.ReduceMotion)
        {
            Finish(block);
            return;
        }

        block.Text = tween.To.Format(NumberTween.ValueAt(tween.From, tween.To.Value, progress));
    }

    private static void Finish(TextBlock block)
    {
        if (!States.TryGetValue(block, out var state) || state.Active is not { } tween) return;
        state.Active = null;
        state.Cancel?.Cancel();
        state.Cancel = null;
        block.Text = tween.TargetText;
    }
}
