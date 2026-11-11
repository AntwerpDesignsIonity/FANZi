using System;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>How the segments of one zone are coloured.</summary>
public enum ZonePattern
{
    /// <summary>Every LED the zone colour.</summary>
    Solid,
    /// <summary>Colour A → colour B along the zone (stepped per segment, smooth with one segment).</summary>
    Gradient,
    /// <summary>Segments alternate colour A / colour B.</summary>
    Alternate,
    /// <summary>One lit segment runs along the zone over a dim background.</summary>
    Chase,
    /// <summary>Colour A at the ends, colour B in the middle.</summary>
    Mirror,
}

/// <summary>Per-zone render settings — the committed copy the frame loop reads.</summary>
public readonly record struct ZoneRender(
    bool Enabled, int Segments, ZonePattern Pattern, bool Reverse, double Brightness, double Speed)
{
    public static readonly ZoneRender Default = new(true, 1, ZonePattern.Solid, false, 1.0, 1.0);

    /// <summary>True when one colour for the whole zone is enough (the cheap per-zone hardware update).</summary>
    public bool IsPlain => Enabled && Pattern == ZonePattern.Solid;
}

/// <summary>
/// 4.5 segments: splits a zone (a RAM stick, a fan ring, an ARGB strip, a case light bar) into up to
/// <see cref="MaxSegments"/> equal blocks and colours them by pattern. Pure — unit-tested; the frame loop concatenates
/// the per-zone arrays into one per-LED update per device.
/// </summary>
public static class ZoneSegmentRenderer
{
    public const int MaxSegments = 8;
    /// <summary>Chase background level relative to the lit segment.</summary>
    public const double ChaseBackground = 0.12;

    public static int ClampSegments(int segments, int ledCount) =>
        Math.Clamp(segments, 1, Math.Max(1, Math.Min(MaxSegments, ledCount)));

    /// <summary>Segment (0-based) an LED belongs to when <paramref name="ledCount"/> LEDs are split into <paramref name="segments"/> blocks.</summary>
    public static int SegmentOf(int led, int ledCount, int segments) =>
        ledCount <= 0 ? 0 : Math.Min(segments - 1, (int)((long)led * segments / ledCount));

    /// <summary>
    /// Fills <paramref name="target"/> (one entry per LED of the zone) for this frame.
    /// <paramref name="a"/> is the zone's effect colour for the frame, <paramref name="b"/> its second colour; both already carry brightness.
    /// </summary>
    public static void Render(Span<RgbColor> target, RgbColor a, RgbColor b, ZoneRender settings, double elapsedSeconds)
    {
        int n = target.Length;
        if (n == 0) return;
        if (!settings.Enabled) { target.Fill(RgbColor.Black); return; }

        int segments = ClampSegments(settings.Segments, n);
        switch (settings.Pattern)
        {
            case ZonePattern.Gradient:
                for (int i = 0; i < n; i++)
                {
                    double t = segments == 1
                        ? (n == 1 ? 0 : (double)i / (n - 1))
                        : (double)SegmentOf(i, n, segments) / (segments - 1);
                    target[i] = RgbColor.Lerp(a, b, t);
                }
                break;

            case ZonePattern.Mirror:
                for (int i = 0; i < n; i++)
                {
                    double pos = segments == 1
                        ? (n == 1 ? 0 : (double)i / (n - 1))
                        : (double)SegmentOf(i, n, segments) / (segments - 1);
                    target[i] = RgbColor.Lerp(a, b, 1.0 - Math.Abs(pos * 2.0 - 1.0));
                }
                break;

            case ZonePattern.Alternate:
            {
                int blocks = Math.Max(2, segments);
                blocks = Math.Min(blocks, n);
                for (int i = 0; i < n; i++)
                    target[i] = SegmentOf(i, n, blocks) % 2 == 0 ? a : b;
                break;
            }

            case ZonePattern.Chase:
            {
                int blocks = Math.Min(n, Math.Max(2, segments));
                // One block per 0.35 s at speed 1.
                int lit = (int)Math.Floor(Math.Max(0, elapsedSeconds) * Math.Max(0.05, settings.Speed) / 0.35) % blocks;
                var dim = a.Scale(ChaseBackground);
                for (int i = 0; i < n; i++)
                    target[i] = SegmentOf(i, n, blocks) == lit ? a : dim;
                break;
            }

            default:
                target.Fill(a);
                break;
        }

        if (settings.Reverse) target.Reverse();
    }

    public static RgbColor[] Render(int ledCount, RgbColor a, RgbColor b, ZoneRender settings, double elapsedSeconds)
    {
        var result = new RgbColor[Math.Max(0, ledCount)];
        Render(result, a, b, settings, elapsedSeconds);
        return result;
    }
}
