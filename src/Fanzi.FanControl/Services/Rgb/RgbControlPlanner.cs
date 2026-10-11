using System;
using System.Collections.Generic;
using System.Linq;

using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>How FANZi pushes colour to one OpenRGB device.</summary>
public enum RgbControlKind
{
    /// <summary>A per-LED mode (Direct/Custom/…): colours go out with UpdateLeds every frame.</summary>
    PerLed,

    /// <summary>
    /// No per-LED mode (e.g. ZOTAC / many GPUs, some RAM): the colour is a parameter of a mode such
    /// as "Static", so it is written with UpdateMode(mode, colors). UpdateLeds is ignored by these.
    /// </summary>
    ModeColor,

    /// <summary>The device exposes no colour-capable mode (effects only).</summary>
    Unsupported,
}

/// <summary>OpenRGB colour modes (matches OpenRGB.NET's ColorMode values).</summary>
public enum RgbModeColorMode
{
    None = 0,
    PerLed = 1,
    ModeSpecific = 2,
    Random = 3,
}

/// <summary>Library-independent view of one OpenRGB mode, so planning is unit-testable.</summary>
public sealed record RgbModeDescriptor(int Index, string Name, RgbModeColorMode ColorMode, uint ColorMin, uint ColorMax);

/// <summary>The chosen control path for a device.</summary>
public sealed record RgbDeviceControl(RgbControlKind Kind, int ModeIndex, string ModeName, int ModeColorCount)
{
    public static RgbDeviceControl None { get; } = new(RgbControlKind.Unsupported, -1, "", 0);

    /// <summary>Device that reports no LEDs (e.g. a RAM stick OpenRGB could not read while another app holds SMBus).</summary>
    public static RgbDeviceControl NoLeds { get; } =
        new(RgbControlKind.Unsupported, -1, "", 0) { Note = "No LEDs reported — close other RGB apps, then Rescan" };

    /// <summary>Optional explanation shown instead of the default label.</summary>
    public string? Note { get; init; }

    /// <summary>Short label for the UI ("Per-LED · Direct", "Single colour · Static").</summary>
    public string Label => Note ?? Kind switch
    {
        RgbControlKind.PerLed => $"Per-LED · {ModeName}",
        RgbControlKind.ModeColor => $"Single colour · {ModeName}",
        _ => "Effects only (no colour control)",
    };
}

/// <summary>
/// Picks the best colour path for a device from its modes:
/// 1. a per-LED mode — "Direct", then "Custom", then "Static", then any per-LED mode;
/// 2. otherwise a mode-specific-colour mode — "Static" first, then the mode needing the fewest colours;
/// 3. otherwise unsupported.
/// Name matching on its own is never trusted: a "Static" mode whose colours are mode-specific is
/// a ModeColor device (this was why ZOTAC GPUs never changed colour).
/// </summary>
public static class RgbControlPlanner
{
    private static readonly string[] PerLedPreference = { "Direct", "Custom", "Static" };

    /// <param name="ledCount">LEDs the device reports; 0 makes per-LED control impossible. -1 = unknown.</param>
    public static RgbDeviceControl Plan(IReadOnlyList<RgbModeDescriptor> modes, int ledCount = -1)
    {
        var plan = PlanModes(modes);
        return ledCount == 0 && plan.Kind == RgbControlKind.PerLed ? RgbDeviceControl.NoLeds : plan;
    }

    private static RgbDeviceControl PlanModes(IReadOnlyList<RgbModeDescriptor> modes)
    {
        if (modes is null || modes.Count == 0) return RgbDeviceControl.None;

        var perLed = modes.Where(m => m.ColorMode == RgbModeColorMode.PerLed).ToList();
        if (perLed.Count > 0)
        {
            var pick = PerLedPreference
                .Select(name => perLed.FirstOrDefault(m => NameIs(m, name)))
                .FirstOrDefault(m => m is not null) ?? perLed[0];
            return new RgbDeviceControl(RgbControlKind.PerLed, pick.Index, pick.Name, 0);
        }

        var modeColor = modes.Where(m => m.ColorMode == RgbModeColorMode.ModeSpecific && m.ColorMax > 0).ToList();
        if (modeColor.Count > 0)
        {
            var pick = modeColor.FirstOrDefault(m => NameIs(m, "Static"))
                ?? modeColor.OrderBy(m => Math.Max(m.ColorMin, 1u)).ThenBy(m => m.Index).First();
            int count = (int)Math.Clamp(Math.Max(pick.ColorMin, 1u), 1u, pick.ColorMax);
            return new RgbDeviceControl(RgbControlKind.ModeColor, pick.Index, pick.Name, count);
        }

        return RgbDeviceControl.None;
    }

    /// <summary>Average of a frame — what a single-colour device shows for a multi-LED effect frame.</summary>
    public static (byte R, byte G, byte B) Average(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        if (colors is null || colors.Count == 0) return (0, 0, 0);
        long r = 0, g = 0, b = 0;
        foreach (var c in colors) { r += c.R; g += c.G; b += c.B; }
        int n = colors.Count;
        return ((byte)((r + n / 2) / n), (byte)((g + n / 2) / n), (byte)((b + n / 2) / n));
    }

    private static bool NameIs(RgbModeDescriptor m, string name) =>
        string.Equals(m.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
