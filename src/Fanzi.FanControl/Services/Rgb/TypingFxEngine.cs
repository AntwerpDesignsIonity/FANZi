using System;
using System.Collections.Generic;
using System.Linq;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>Typing FX animation styles (4.3).</summary>
public enum TypingFxStyle
{
    /// <summary>Expanding ring from every key you press; rings cross each other on fast typing.</summary>
    Ripple,
    /// <summary>The whole device flashes on each key and breathes with your typing speed.</summary>
    Pulse,
    /// <summary>A beam shoots left and right (and up/down) from the key you pressed.</summary>
    Line,
    /// <summary>A soft glowing area blooms around each key and fades.</summary>
    Area,
    /// <summary>Sparks scatter from the key and fade — a tiny firework per keystroke.</summary>
    Fireworks,
    /// <summary>A cursor walks across the LEDs with every key; Enter / Space sends a carriage-return wave.</summary>
    Typewriter,
    /// <summary>Each key drops a glowing trail down its column, Matrix-rain style.</summary>
    Matrix,
    /// <summary>The faster you type the hotter the colours — idle blue, flow orange, frenzy white.</summary>
    Heat,
}

/// <summary>One keystroke the engine animates.</summary>
public readonly record struct KeyHit(double TimeSeconds, LedPoint Origin, bool IsBreak, int Seed);

/// <summary>
/// Pure, time-based keystroke animation (4.3 "Typing FX"). Feed it key hits with <see cref="Press"/>, then ask for a
/// frame per device with <see cref="Render"/>. Everything is a function of (now - hitTime) and LED position, so it
/// is deterministic and unit-testable. Effects are layered over the device's base colour (the current RGB effect),
/// so the lights keep doing what they did and typing paints on top.
/// </summary>
public sealed class TypingFxEngine
{
    private readonly List<KeyHit> _hits = new();
    private readonly object _lock = new();
    private int _cursor;          // Typewriter
    private double _lastHitTime;  // Heat / Pulse rate
    private double _rateBase;     // exponentially smoothed keys per second at the last hit
    private double _rate;         // _rateBase released over time (see Tick)

    /// <summary>How long one key's effect lives, seconds.</summary>
    public const double HitLifetime = 1.6;

    /// <summary>Most hits kept at once (fast typists).</summary>
    public const int MaxHits = 48;

    public TypingFxStyle Style { get; set; } = TypingFxStyle.Ripple;

    /// <summary>0.5 – 2.0: ring speed / fade speed.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>0 – 1: how much of the base colour stays visible under the effect.</summary>
    public double BackgroundLevel { get; set; } = 0.35;

    /// <summary>Smoothed typing rate in keys per second (what Heat / Pulse breathe with).</summary>
    public double KeysPerSecond { get { lock (_lock) return _rate; } }

    /// <summary>Approximate words per minute (5 chars per word).</summary>
    public double WordsPerMinute => KeysPerSecond * 60.0 / 5.0;

    public int ActiveHits { get { lock (_lock) return _hits.Count; } }

    /// <summary>Registers a keystroke. <paramref name="origin"/> is the key's position on the keyboard (0..1), or
    /// null when unknown (origin is then pseudo-random so every device still reacts).</summary>
    public void Press(double now, LedPoint? origin, bool isBreak = false, int seed = 0)
    {
        lock (_lock)
        {
            double gap = _lastHitTime > 0 ? Math.Max(0.03, now - _lastHitTime) : 1.0;
            double instant = 1.0 / gap;
            _rateBase = _lastHitTime > 0 ? _rate * 0.7 + Math.Min(instant, 15) * 0.3 : Math.Min(instant, 3);
            _rate = _rateBase;
            _lastHitTime = now;

            var o = origin ?? new LedPoint(Hash01(seed * 7919 + _hits.Count), Hash01(seed * 104729 + 13));
            _hits.Add(new KeyHit(now, o, isBreak, seed));
            if (_hits.Count > MaxHits) _hits.RemoveRange(0, _hits.Count - MaxHits);
            _cursor = isBreak ? 0 : _cursor + 1;
        }
    }

    /// <summary>Drops expired hits and decays the typing rate; call once per frame.</summary>
    public void Tick(double now)
    {
        lock (_lock)
        {
            _hits.RemoveAll(h => now - h.TimeSeconds > HitLifetime / Math.Max(0.25, Speed));
            // Rate holds for 0.4 s after the last key, then releases with a 1.2 s time constant.
            if (_lastHitTime > 0)
                _rate = _rateBase * Math.Exp(-Math.Max(0, now - _lastHitTime - 0.4) / 1.2);
        }
    }

    /// <summary>
    /// Colours for one device: <paramref name="layout"/> gives every LED a position, <paramref name="baseColor"/> is
    /// what the LED shows when nothing is happening. <paramref name="isKeyboard"/> devices use the key's true
    /// position; other devices receive the hit remapped along their own axis so RAM, strips and GPUs join in.
    /// </summary>
    public RgbColor[] Render(double now, LedLayout layout, RgbColor baseColor, RgbColor primary, RgbColor secondary, bool isKeyboard)
    {
        KeyHit[] hits;
        double rate;
        int cursor;
        lock (_lock) { hits = _hits.ToArray(); rate = _rate; cursor = _cursor; }

        var pts = layout.Points;
        var frame = new RgbColor[pts.Count];
        var dim = baseColor.Scale(BackgroundLevel);
        double speed = Math.Clamp(Speed, 0.25, 3.0);

        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            double energy = 0;     // 0..1+ effect strength at this LED
            double hue = 0;        // 0..1 blend from primary → secondary

            switch (Style)
            {
                case TypingFxStyle.Ripple:
                    foreach (var h in hits)
                    {
                        double age = (now - h.TimeSeconds) * speed;
                        double radius = age * 1.1;                     // board widths per second
                        double d = Dist(p, Origin(h, isKeyboard, layout));
                        double ring = Math.Exp(-Math.Pow((d - radius) / 0.07, 2));
                        double life = Math.Max(0, 1 - age / HitLifetime);
                        energy += ring * life;
                        hue += ring * life * Math.Min(1, age);
                    }
                    break;

                case TypingFxStyle.Pulse:
                    foreach (var h in hits)
                    {
                        double age = (now - h.TimeSeconds) * speed;
                        energy += Math.Exp(-age * 4.5);
                    }
                    hue = Math.Clamp(rate / 8.0, 0, 1);
                    break;

                case TypingFxStyle.Line:
                    foreach (var h in hits)
                    {
                        double age = (now - h.TimeSeconds) * speed;
                        var o = Origin(h, isKeyboard, layout);
                        double reach = age * 1.6;
                        double life = Math.Max(0, 1 - age / 0.9);
                        // horizontal beam travelling outward from the key's column …
                        double dx = Math.Abs(p.X - o.X), dy = Math.Abs(p.Y - o.Y);
                        double beamH = Math.Exp(-Math.Pow((dx - reach) / 0.05, 2)) * Math.Exp(-Math.Pow(dy / 0.09, 2));
                        // … and a shorter vertical one on matrix devices
                        double beamV = layout.IsMatrix ? Math.Exp(-Math.Pow((dy - reach * 0.6) / 0.08, 2)) * Math.Exp(-Math.Pow(dx / 0.05, 2)) : 0;
                        energy += (beamH + beamV * 0.7) * life;
                        hue += beamH * life;
                    }
                    break;

                case TypingFxStyle.Area:
                    foreach (var h in hits)
                    {
                        double age = (now - h.TimeSeconds) * speed;
                        double d = Dist(p, Origin(h, isKeyboard, layout));
                        double sigma = 0.10 + age * 0.08;              // the blob grows as it fades
                        double life = Math.Max(0, 1 - age / 1.2);
                        energy += Math.Exp(-Math.Pow(d / sigma, 2)) * life;
                        hue += Math.Exp(-Math.Pow(d / sigma, 2)) * life * (0.5 + 0.5 * Hash01(h.Seed));
                    }
                    break;

                case TypingFxStyle.Fireworks:
                    foreach (var h in hits)
                    {
                        double age = (now - h.TimeSeconds) * speed;
                        var o = Origin(h, isKeyboard, layout);
                        double life = Math.Max(0, 1 - age / 1.1);
                        for (int s = 0; s < 6; s++)
                        {
                            double ang = Hash01(h.Seed * 31 + s) * Math.Tau;
                            double spd = 0.5 + Hash01(h.Seed * 17 + s) * 0.9;
                            var spark = new LedPoint(o.X + Math.Cos(ang) * spd * age, o.Y + Math.Sin(ang) * spd * age * (layout.IsMatrix ? 1 : 0));
                            double d = Dist(p, spark);
                            energy += Math.Exp(-Math.Pow(d / 0.045, 2)) * life;
                            hue += Math.Exp(-Math.Pow(d / 0.045, 2)) * life * Hash01(h.Seed + s);
                        }
                        // the flash at the origin
                        energy += Math.Exp(-Math.Pow(Dist(p, o) / 0.06, 2)) * Math.Exp(-age * 8);
                    }
                    break;

                case TypingFxStyle.Typewriter:
                {
                    int n = pts.Count;
                    int pos = n > 0 ? cursor % n : 0;
                    // cursor cell glows; a short trail behind it
                    int behind = ((i - pos) % n + n) % n;
                    double trail = behind == 0 ? 1.0 : behind > n - 4 ? 0.35 * (behind - (n - 4)) / 3.0 : 0;
                    if (!layout.IsMatrix || true) energy += trail;
                    foreach (var h in hits)
                    {
                        double age = (now - h.TimeSeconds) * speed;
                        if (h.IsBreak)
                        {
                            // carriage return: a wave sweeping right→left across the whole device
                            double wave = Math.Exp(-Math.Pow(((1 - age * 1.5) - p.X) / 0.08, 2)) * Math.Max(0, 1 - age / 0.9);
                            energy += wave; hue += wave;
                        }
                        else
                        {
                            energy += Math.Exp(-age * 7) * 0.25; // tick flash
                        }
                    }
                    break;
                }

                case TypingFxStyle.Matrix:
                    foreach (var h in hits)
                    {
                        double age = (now - h.TimeSeconds) * speed;
                        var o = Origin(h, isKeyboard, layout);
                        double head = o.Y + age * 1.4;                 // falls down the column
                        double dx = Math.Abs(p.X - o.X);
                        double col = Math.Exp(-Math.Pow(dx / (layout.IsMatrix ? 0.045 : 0.08), 2));
                        double behind = head - p.Y;                    // >0 ⇒ the drop already passed this LED
                        double trail = behind >= -0.03 && behind < 0.5 ? Math.Exp(-behind * 4.5) : 0;
                        double life = Math.Max(0, 1 - age / 1.3);
                        energy += col * trail * life;
                        hue += col * trail * life * (behind < 0.05 ? 0 : 1); // white head, green tail
                    }
                    break;

                case TypingFxStyle.Heat:
                {
                    double heat = Math.Clamp(rate / 9.0, 0, 1);  // ~108 WPM = white hot
                    foreach (var h in hits)
                    {
                        double age = (now - h.TimeSeconds) * speed;
                        double d = Dist(p, Origin(h, isKeyboard, layout));
                        energy += Math.Exp(-Math.Pow(d / 0.12, 2)) * Math.Exp(-age * 2.5);
                    }
                    energy = Math.Min(1.4, energy + heat * 0.8);
                    hue = heat;
                    break;
                }
            }

            energy = Math.Clamp(energy, 0, 1.4);
            double t = Math.Clamp(hue / Math.Max(energy, 0.001), 0, 1);
            RgbColor fx = Style switch
            {
                TypingFxStyle.Heat => HeatColor(t),
                TypingFxStyle.Matrix => RgbColor.Lerp(RgbColor.White, new RgbColor(40, 255, 90), t),
                _ => RgbColor.Lerp(primary, secondary, t),
            };
            double mix = Math.Clamp(energy, 0, 1);
            var lit = RgbColor.Lerp(dim, fx, mix);
            if (energy > 1) lit = RgbColor.Lerp(lit, RgbColor.White, Math.Min(0.6, energy - 1)); // over-bright crossings go white
            frame[i] = lit;
        }
        return frame;
    }

    /// <summary>Base colour the engine shows between keystrokes (the running effect, dimmed).</summary>
    public RgbColor Idle(RgbColor baseColor) => baseColor.Scale(BackgroundLevel);

    private static LedPoint Origin(KeyHit h, bool isKeyboard, LedLayout layout)
    {
        if (isKeyboard || layout.IsMatrix) return h.Origin;
        // Linear devices: the key's column becomes a position along the strip; Y is the strip's single row.
        return new LedPoint(h.Origin.X, 0.5);
    }

    private static double Dist(LedPoint a, LedPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Deterministic pseudo-random 0..1 from an int.</summary>
    internal static double Hash01(int n)
    {
        unchecked
        {
            uint x = (uint)n * 2654435761u;
            x ^= x >> 15; x *= 2246822519u; x ^= x >> 13;
            return (x & 0xFFFFFF) / (double)0x1000000;
        }
    }

    /// <summary>Idle blue → flow orange → frenzy white.</summary>
    internal static RgbColor HeatColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5
            ? RgbColor.Lerp(new RgbColor(0, 90, 255), new RgbColor(255, 120, 0), t * 2)
            : RgbColor.Lerp(new RgbColor(255, 120, 0), RgbColor.White, (t - 0.5) * 2);
    }

    public static string Label(TypingFxStyle s) => s switch
    {
        TypingFxStyle.Ripple => "Ripple",
        TypingFxStyle.Pulse => "Pulse",
        TypingFxStyle.Line => "Line",
        TypingFxStyle.Area => "Area",
        TypingFxStyle.Fireworks => "Fireworks",
        TypingFxStyle.Typewriter => "Typewriter",
        TypingFxStyle.Matrix => "Matrix rain",
        TypingFxStyle.Heat => "Typing heat",
        _ => s.ToString(),
    };

    public static string Hint(TypingFxStyle s) => s switch
    {
        TypingFxStyle.Ripple => "A ring spreads from every key; fast typing makes rings collide and flash white",
        TypingFxStyle.Pulse => "Every key flashes all LEDs; colour drifts to the secondary as you speed up",
        TypingFxStyle.Line => "Beams shoot sideways (and up/down on the keyboard) from the key you hit",
        TypingFxStyle.Area => "A soft glow blooms around the key and melts away",
        TypingFxStyle.Fireworks => "Six sparks scatter from each key — a tiny firework per keystroke",
        TypingFxStyle.Typewriter => "A cursor walks the LEDs with each key; Enter or Space rolls a carriage-return wave",
        TypingFxStyle.Matrix => "Each key drops a green trail down its column, white head first",
        TypingFxStyle.Heat => "Idle blue, steady orange, 100+ WPM white hot — your typing speed as colour",
        _ => "",
    };

    public static TypingFxStyle ParseStyle(string? name) =>
        Enum.TryParse<TypingFxStyle>(name?.Replace(" rain", "").Replace("Typing heat", "Heat"), true, out var s) ? s : TypingFxStyle.Ripple;
}
