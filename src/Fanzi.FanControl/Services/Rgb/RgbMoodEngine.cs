using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>A lighting scene produced from a text prompt.</summary>
public sealed record RgbMoodResult(
    RgbEffectType Effect,
    RgbColor Primary,
    RgbColor Secondary,
    double Speed,
    double Brightness,
    string Explanation);

/// <summary>
/// AEDi "describe a mood" lighting generator (v4). Runs fully offline: the prompt is tokenised and scored
/// against a vocabulary of colour words, scene words and energy words; the best matches decide the
/// colours, the effect and its speed / brightness. Unknown prompts still get a stable, unique palette
/// (hue derived from the text) so the same words always give the same scene.
/// </summary>
public static class RgbMoodEngine
{
    private sealed record Scene(string[] Keys, RgbColor A, RgbColor B, RgbEffectType Effect, double Speed, double Brightness, string Name);

    // Scene vocabulary — the first matching keys win, weighted by how many words of the prompt they cover.
    private static readonly Scene[] Scenes =
    [
        new(["ocean", "sea", "wave", "beach", "water", "aqua", "deep"], new(0, 90, 255), new(0, 220, 255), RgbEffectType.ColorWave, 0.8, 0.95, "ocean"),
        new(["fire", "flame", "lava", "inferno", "volcano", "burn", "hot", "magma"], new(255, 40, 0), new(255, 160, 0), RgbEffectType.ColorWave, 1.4, 1.0, "fire"),
        new(["forest", "nature", "jungle", "leaf", "tree", "garden", "earth"], new(0, 200, 60), new(120, 255, 40), RgbEffectType.Pulse, 0.7, 0.9, "forest"),
        new(["sunset", "dusk", "evening", "golden"], new(255, 90, 0), new(180, 0, 200), RgbEffectType.ColorWave, 0.7, 0.95, "sunset"),
        new(["sunrise", "dawn", "morning"], new(255, 150, 60), new(255, 220, 140), RgbEffectType.Pulse, 0.6, 0.9, "sunrise"),
        new(["ice", "frozen", "arctic", "snow", "winter", "glacier", "frost", "cold"], new(140, 210, 255), new(255, 255, 255), RgbEffectType.Static, 0.6, 0.85, "ice"),
        new(["galaxy", "space", "cosmic", "nebula", "stars", "universe", "astral"], new(90, 0, 255), new(255, 0, 170), RgbEffectType.ColorWave, 0.6, 0.9, "galaxy"),
        new(["cyberpunk", "neon", "synthwave", "retro", "vaporwave", "tokyo"], new(255, 0, 200), new(0, 230, 255), RgbEffectType.DualColorFlash, 1.2, 1.0, "neon"),
        new(["matrix", "hacker", "code", "terminal"], new(0, 255, 70), new(0, 90, 20), RgbEffectType.Pulse, 1.3, 0.9, "matrix"),
        new(["party", "rave", "club", "dance", "disco", "celebrate"], new(255, 0, 0), new(0, 0, 255), RgbEffectType.Rainbow, 2.2, 1.0, "party"),
        new(["rainbow", "spectrum", "pride", "colorful", "colourful", "unicorn"], new(255, 0, 0), new(0, 0, 255), RgbEffectType.Rainbow, 1.0, 1.0, "rainbow"),
        new(["calm", "relax", "chill", "zen", "peace", "sleep", "meditate", "soft", "cozy", "cosy"], new(60, 90, 255), new(140, 60, 255), RgbEffectType.Pulse, 0.4, 0.55, "calm"),
        new(["focus", "work", "study", "productive", "office", "read"], new(255, 244, 229), new(150, 200, 255), RgbEffectType.Static, 0.5, 0.8, "focus"),
        new(["gaming", "game", "fps", "battle", "esports", "competitive", "raid"], new(255, 0, 40), new(0, 120, 255), RgbEffectType.Performance, 1.2, 1.0, "gaming"),
        new(["alert", "warning", "danger", "alarm", "emergency", "police"], new(255, 0, 0), new(0, 40, 255), RgbEffectType.DualColorFlash, 2.4, 1.0, "alert"),
        new(["christmas", "xmas", "festive", "holiday"], new(255, 0, 20), new(0, 200, 60), RgbEffectType.DualColorFlash, 0.8, 0.95, "christmas"),
        new(["halloween", "spooky", "pumpkin", "witch"], new(255, 90, 0), new(110, 0, 200), RgbEffectType.Pulse, 1.0, 0.9, "halloween"),
        new(["love", "romantic", "valentine", "heart", "rose"], new(255, 0, 90), new(255, 120, 170), RgbEffectType.Pulse, 0.6, 0.85, "romance"),
        new(["storm", "thunder", "lightning", "electric"], new(200, 220, 255), new(40, 0, 160), RgbEffectType.Strobe, 1.8, 1.0, "storm"),
        new(["ionity", "aedi", "antwerp"], new(37, 99, 235), new(34, 211, 238), RgbEffectType.ColorWave, 0.9, 1.0, "Ionity"),
        new(["southafrica", "springbok", "springboks", "bokke", "mzansi"], new(0, 122, 77), new(255, 184, 28), RgbEffectType.DualColorFlash, 0.8, 1.0, "South Africa"),
        new(["temperature", "thermal", "heat", "temps"], RgbColor.IceBlue, RgbColor.Red, RgbEffectType.TemperatureReactive, 1.0, 1.0, "thermal"),
        new(["performance", "load", "benchmark", "render", "stress"], RgbColor.Blue, RgbColor.Red, RgbEffectType.Performance, 1.0, 1.0, "performance"),
        new(["royal", "luxury", "elegant", "king", "queen"], new(110, 0, 255), new(255, 200, 0), RgbEffectType.Pulse, 0.5, 0.9, "royal"),
        new(["blood", "vampire", "crimson", "dark"], new(170, 0, 20), new(60, 0, 0), RgbEffectType.Pulse, 0.6, 0.85, "crimson"),
        new(["candy", "pastel", "cute", "kawaii", "bubblegum"], new(255, 140, 220), new(140, 220, 255), RgbEffectType.ColorWave, 0.7, 0.8, "pastel"),
        new(["night", "midnight", "moon", "late"], new(20, 30, 140), new(90, 40, 200), RgbEffectType.Pulse, 0.35, 0.35, "night"),
    ];

    // Plain colour words (used when no scene matched, or to recolour a scene: "red galaxy").
    private static readonly Dictionary<string, RgbColor> Colours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = RgbColor.Red, ["orange"] = RgbColor.Orange, ["yellow"] = RgbColor.Yellow, ["gold"] = new(255, 190, 0),
        ["green"] = RgbColor.Green, ["lime"] = new(150, 255, 0), ["teal"] = RgbColor.Teal, ["cyan"] = RgbColor.Cyan,
        ["blue"] = RgbColor.Blue, ["navy"] = new(0, 30, 160), ["purple"] = RgbColor.Purple, ["violet"] = new(150, 60, 255),
        ["magenta"] = RgbColor.Magenta, ["pink"] = new(255, 80, 170), ["white"] = RgbColor.White, ["warm"] = new(255, 170, 90),
        ["amber"] = new(255, 140, 0), ["mint"] = new(80, 255, 170), ["turquoise"] = new(0, 220, 200), ["indigo"] = new(75, 0, 200),
        ["silver"] = new(200, 210, 230), ["coral"] = new(255, 110, 80), ["rose"] = new(255, 60, 120),
    };

    private static readonly string[] FastWords = ["fast", "energetic", "hype", "intense", "crazy", "wild", "quick", "aggressive", "turbo"];
    private static readonly string[] SlowWords = ["slow", "gentle", "subtle", "lazy", "smooth", "breathing", "soft", "quiet"];
    private static readonly string[] DimWords = ["dim", "low", "subtle", "night", "dark", "faint"];
    private static readonly string[] BrightWords = ["bright", "max", "vivid", "loud", "blinding", "full"];
    private static readonly string[] StaticWords = ["static", "solid", "still", "steady", "plain"];

    /// <summary>Builds a lighting scene for <paramref name="prompt"/> (never throws; empty → Ionity default).</summary>
    public static RgbMoodResult Generate(string? prompt)
    {
        var words = Tokenise(prompt);
        if (words.Count == 0)
            return new RgbMoodResult(RgbEffectType.ColorWave, new(37, 99, 235), new(34, 211, 238), 0.9, 1.0,
                "Type a mood such as \"calm ocean\", \"red galaxy\" or \"fast cyberpunk party\".");

        var joined = string.Join("", words);
        Scene? best = null;
        int bestScore = 0;
        foreach (var scene in Scenes)
        {
            int score = scene.Keys.Count(k => words.Contains(k) || (k.Length > 6 && joined.Contains(k, StringComparison.Ordinal)));
            if (score > bestScore) { best = scene; bestScore = score; }
        }

        var colourHits = words.Where(Colours.ContainsKey).Select(w => Colours[w]).ToList();

        RgbEffectType effect;
        RgbColor a, b;
        double speed, brightness;
        var why = new StringBuilder();

        if (best is not null)
        {
            effect = best.Effect; a = best.A; b = best.B; speed = best.Speed; brightness = best.Brightness;
            why.Append($"Matched the \"{best.Name}\" scene");
            if (colourHits.Count > 0)
            {
                a = colourHits[0];
                if (colourHits.Count > 1) b = colourHits[1];
                why.Append(colourHits.Count > 1 ? " recoloured with your two colours" : " recoloured with your colour");
            }
        }
        else if (colourHits.Count > 0)
        {
            a = colourHits[0];
            b = colourHits.Count > 1 ? colourHits[1] : RgbColorHarmony.Partner(a, RgbColorHarmony.Rule.Analogous);
            effect = colourHits.Count > 1 ? RgbEffectType.ColorWave : RgbEffectType.Pulse;
            speed = 1.0; brightness = 1.0;
            why.Append(colourHits.Count > 1 ? "Blended your two colours" : "Built around your colour with an analogous partner");
        }
        else
        {
            // Unknown words: a stable hue from the text, triadic partner.
            uint hash = 2166136261;
            foreach (char ch in joined) hash = (hash ^ ch) * 16777619;
            double hue = hash % 360;
            a = RgbColor.FromHsv(hue, 0.9, 1.0);
            b = RgbColorHarmony.Partner(a, RgbColorHarmony.Rule.Triadic);
            effect = RgbEffectType.ColorWave; speed = 1.0; brightness = 0.95;
            why.Append($"No known mood words, so AEDi derived a unique palette (hue {hue:F0}°) from your text");
        }

        if (words.Any(w => StaticWords.Contains(w))) { effect = RgbEffectType.Static; why.Append(", held static"); }
        if (words.Any(w => FastWords.Contains(w))) { speed = Math.Min(3.0, speed * 1.8 + 0.2); why.Append(", sped up"); }
        if (words.Any(w => SlowWords.Contains(w))) { speed = Math.Max(0.2, speed * 0.55); why.Append(", slowed down"); }
        if (words.Any(w => DimWords.Contains(w))) { brightness = Math.Min(brightness, 0.4); why.Append(", dimmed"); }
        if (words.Any(w => BrightWords.Contains(w))) { brightness = 1.0; why.Append(", full brightness"); }

        why.Append($" → {Describe(effect)}, {a.ToHex()} + {b.ToHex()}.");
        return new RgbMoodResult(effect, a, b, Math.Round(speed, 2), Math.Round(brightness, 2), why.ToString());
    }

    internal static string Describe(RgbEffectType effect) => effect switch
    {
        RgbEffectType.ColorWave => "colour wave",
        RgbEffectType.DualColorFlash => "dual-colour flash",
        RgbEffectType.TemperatureReactive => "temperature reactive",
        RgbEffectType.CpuLoadReactive => "CPU-load reactive",
        _ => effect.ToString().ToLowerInvariant(),
    };

    private static List<string> Tokenise(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return new List<string>();
        var sb = new StringBuilder(prompt.Length);
        foreach (char ch in prompt.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
