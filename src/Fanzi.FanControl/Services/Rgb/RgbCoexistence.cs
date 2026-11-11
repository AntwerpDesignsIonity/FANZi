using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>Who drives SMBus lighting (RAM sticks, onboard LEDs wired to the board's SMBus).</summary>
public enum SmbusPolicy
{
    /// <summary>The vendor's own app when it is running, otherwise FANZi.</summary>
    Auto,
    /// <summary>FANZi never touches SMBus lighting.</summary>
    Vendor,
    /// <summary>FANZi drives it. Only safe once the vendor app is stopped — FANZi says so, it does not stop anything itself.</summary>
    Fanzi,
}

/// <summary>The outcome for this moment: does FANZi keep its hands off SMBus lighting, and why.</summary>
public sealed record SmbusDecision(bool HandsOff, IReadOnlyList<string> Owners, string Title, string Message, bool Risky);

/// <summary>
/// 4.5 RGB coexistence. RAM lighting has exactly one safe writer. Motherboard and RAM vendor apps (Aura / LightingService,
/// Mystic Light, RGB Fusion, Polychrome, iCUE, the RAM makers' own tools) drive the sticks over the board's
/// SMBus from a background service. Up to 4.4 FANZi started OpenRGB next to them: OpenRGB re-addresses ENE controllers
/// while it scans the bus and then streams frames, so two masters interleaved writes on one bus — the sticks froze or lost
/// their effect until a cold boot, and the vendor app showed no RAM any more.
///
/// From 4.5 the default is: when such an app or its service is running, FANZi leaves SMBus lighting to it — OpenRGB is
/// started with its SMBus RAM / board detectors off and FANZi writes nothing to those devices. USB devices (keyboard,
/// mouse, hubs, monitor) and the GPU keep working. Nothing is ever closed automatically.
/// </summary>
public static class RgbCoexistence
{
    /// <summary>Products that own SMBus lighting when they run (names as produced by <see cref="RgbConflictDetector"/>).</summary>
    /// <remarks>
    /// Only the components that write the bus. The vendor shells (Armoury Crate, MSI Center, Gigabyte Control Center) are
    /// not owners on their own: they light nothing until their lighting part runs (LightingService / Aura, Mystic Light's
    /// LEDKeeper2, RGB Fusion), and they are often installed for a GPU or updates on a board of another make.
    /// </remarks>
    internal static readonly HashSet<string> SmbusOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASUS Aura", "ASUS Aura (LightingService)",
        "MSI Mystic Light",
        "Gigabyte RGB Fusion",
        "ASRock Polychrome",
        "Corsair iCUE",
        "SignalRGB",
        "G.SKILL Trident Z Lighting", "Kingston FURY CTRL", "TEAMGROUP T-FORCE BLITZ", "XPG Prime", "Crucial Ballistix MOD",
        "Patriot Viper RGB", "Thermaltake TT RGB Plus", "another OpenRGB instance",
    };

    /// <summary>
    /// OpenRGB detectors that probe or write the motherboard SMBus (exact names from OpenRGB 1.0's detector list). Turned
    /// off in FANZi's private OpenRGB config while FANZi is hands-off, so OpenRGB does not even scan the bus.
    /// </summary>
    public static readonly IReadOnlyList<string> SmbusDetectors = new[]
    {
        "ENE SMBus DRAM", "Corsair DRAM", "Corsair Vengeance RGB DRAM", "Crucial Ballistix", "HyperX DRAM",
        "Kingston Fury DDR4 DRAM", "Kingston Fury DDR5 DRAM", "T-Force Xtreem DDR4 DRAM", "Patriot Viper", "Patriot Viper Steel",
        "Gigabyte RGB Fusion 2 DRAM", "Gigabyte RGB Fusion 2 SMBus", "Gigabyte RGB Fusion",
        "ASUS Aura SMBus Motherboard", "ASRock Motherboard SMBus Controllers", "EVGA Motherboard SMBus Controllers",
    };

    public static SmbusPolicy ParsePolicy(string? text) => text switch
    {
        "Vendor" => SmbusPolicy.Vendor,
        "Fanzi" => SmbusPolicy.Fanzi,
        _ => SmbusPolicy.Auto,
    };

    /// <summary>Running products that own SMBus lighting.</summary>
    public static IReadOnlyList<string> OwnersAmong(IEnumerable<string> runningProducts) =>
        runningProducts.Where(p => SmbusOwners.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Pure decision — unit-tested.</summary>
    /// <param name="maxsunOwnsRam">True when FANZi drives RAM through MAXSUN Sync's own service (already single-writer).</param>
    public static SmbusDecision Decide(SmbusPolicy policy, IEnumerable<string> runningProducts, bool maxsunOwnsRam = false)
    {
        var owners = OwnersAmong(runningProducts);
        string who = owners.Count == 0 ? "" : string.Join(", ", owners);

        switch (policy)
        {
            case SmbusPolicy.Vendor:
                return new(true, owners, "RAM and onboard lighting: left to the vendor app",
                    "You chose to keep SMBus lighting (RAM sticks, onboard LEDs) with your motherboard / RAM maker's software. " +
                    "FANZi does not scan or write it; keyboard, mouse, GPU and other USB lighting still follow FANZi.", false);

            case SmbusPolicy.Fanzi:
                return owners.Count == 0
                    ? new(false, owners, "", "", false)
                    : new(false, owners, $"{who} is also driving your RAM lighting",
                        $"You set FANZi to drive RAM and onboard lighting, but {who} is running and writes the same SMBus. Two writers " +
                        "on one bus can freeze the sticks until a cold boot. Stop it below, or switch back to Automatic.", true);

            default:
                if (owners.Count == 0 || maxsunOwnsRam) return new(false, owners, "", "", false);
                return new(true, owners, $"{who} keeps your RAM and onboard lighting",
                    $"{who} is running and already drives RAM / onboard lighting over the motherboard's SMBus. FANZi leaves those to it " +
                    "(a second writer on that bus can freeze the sticks) and lights everything else: keyboard, mouse, GPU, USB hubs, monitor. " +
                    "To have FANZi light the RAM too, stop that app first.", false);
        }
    }
}
