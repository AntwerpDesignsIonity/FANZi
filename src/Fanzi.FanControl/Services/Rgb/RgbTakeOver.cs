using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>
/// 4.6 automatic take-over: which running RGB apps FANZi may close by itself, and how often it tries.
///
/// Only apps whose one job is lighting are closed automatically. Suites that also run fans, pumps, GPU tuning or
/// mouse / keyboard settings (Corsair iCUE, NZXT CAM, Lian Li L-Connect, Thermaltake TT RGB Plus, Armoury Crate,
/// MSI Center, Gigabyte Control Center, ZOTAC FireStorm, Razer Synapse, Logitech G HUB, SteelSeries GG, HyperX NGENUITY,
/// MAXSUN View) are never closed without the user pressing the button: stopping one of those can leave an AIO pump or
/// a fan hub without its controller. They keep the banner with its manual buttons.
/// The close is for this Windows session only — services are stopped, not disabled, and no autostart entry is removed.
/// </summary>
public static class RgbTakeOver
{
    /// <summary>Products that only do lighting — safe to close automatically.</summary>
    internal static readonly IReadOnlySet<string> LightingOnlyProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SignalRGB",
        "ASUS Aura",
        "ASUS Aura (LightingService)",
        "MSI Mystic Light",
        "Gigabyte RGB Fusion",
        "ASRock Polychrome",
        "another OpenRGB instance",
        "ZOTAC Spectra",
        "G.SKILL Trident Z Lighting",
        "Kingston FURY CTRL",
        "TEAMGROUP T-FORCE BLITZ",
        "XPG Prime",
        "Crucial Ballistix MOD",
        "Patriot Viper RGB",
    };

    /// <summary>True when the product may be closed without asking.</summary>
    public static bool IsLightingOnly(string product) => LightingOnlyProducts.Contains(product);

    /// <summary>The conflicts FANZi closes by itself (lighting-only apps).</summary>
    public static IReadOnlyList<RgbConflictDetector.Conflict> AutoClosable(IReadOnlyList<RgbConflictDetector.Conflict> conflicts)
        => conflicts.Where(c => IsLightingOnly(c.Product)).ToArray();

    /// <summary>The conflicts left to the user (suites that also run cooling, tuning or peripherals).</summary>
    public static IReadOnlyList<RgbConflictDetector.Conflict> NeedsUser(IReadOnlyList<RgbConflictDetector.Conflict> conflicts)
        => conflicts.Where(c => !IsLightingOnly(c.Product)).ToArray();
}

/// <summary>
/// Stops a vendor app that keeps coming back from turning into a kill loop: one attempt per <see cref="MinInterval"/>,
/// at most <see cref="MaxAttempts"/> per product per FANZi run. After that the banner stays and the user decides.
/// </summary>
public sealed class RgbTakeOverThrottle
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);
    public const int MaxAttempts = 3;

    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    /// <summary>
    /// Returns the conflicts to close now (possibly empty) and records the attempt. Products that used up their
    /// attempts are left out.
    /// </summary>
    public IReadOnlyList<RgbConflictDetector.Conflict> Acquire(DateTime utcNow, IReadOnlyList<RgbConflictDetector.Conflict> closable)
    {
        if (closable.Count == 0 || utcNow - _lastAttemptUtc < MinInterval)
            return Array.Empty<RgbConflictDetector.Conflict>();

        var allowed = closable.Where(c => _attempts.GetValueOrDefault(c.Product) < MaxAttempts).ToArray();
        if (allowed.Length == 0) return allowed;

        foreach (var c in allowed) _attempts[c.Product] = _attempts.GetValueOrDefault(c.Product) + 1;
        _lastAttemptUtc = utcNow;
        return allowed;
    }

    /// <summary>True once the product was closed <see cref="MaxAttempts"/> times and came back.</summary>
    public bool GaveUpOn(string product) => _attempts.GetValueOrDefault(product) >= MaxAttempts;

    public void Reset()
    {
        _attempts.Clear();
        _lastAttemptUtc = DateTime.MinValue;
    }
}
