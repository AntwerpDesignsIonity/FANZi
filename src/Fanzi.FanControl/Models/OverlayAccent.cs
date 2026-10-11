using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.Models;

/// <summary>Colour preset for the mini overlay's border, glow and title (v4 overlay studio).</summary>
public sealed record OverlayAccent(string Name, string Start, string End, string Glow)
{
    /// <summary>Follows the live RGB effect colour instead of fixed colours.</summary>
    public const string RgbSyncName = "RGB Sync";

    public static IReadOnlyList<OverlayAccent> All { get; } =
    [
        new("Ionity Blue",   "#2563EB", "#22D3EE", "#702563EB"),
        new("Neon Cyan",     "#06B6D4", "#A5F3FC", "#7006B6D4"),
        new("Aurora Green",  "#10B981", "#A3E635", "#7010B981"),
        new("Ember",         "#F97316", "#EF4444", "#70F97316"),
        new("Royal Purple",  "#7C3AED", "#EC4899", "#707C3AED"),
        new("Gold",          "#F59E0B", "#FDE68A", "#70F59E0B"),
        new("Stealth",       "#334155", "#94A3B8", "#40334155"),
        new(RgbSyncName,     "#FF0040", "#00C8FF", "#70FF0040"),
    ];

    public static IReadOnlyList<string> Names { get; } = All.Select(a => a.Name).ToArray();

    /// <summary>The preset with this name (case-insensitive), else the default Ionity Blue.</summary>
    public static OverlayAccent ByName(string? name)
        => All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public bool IsRgbSync => Name == RgbSyncName;
}
