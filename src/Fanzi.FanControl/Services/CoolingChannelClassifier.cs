using System;
using System.Collections.Generic;
using System.Linq;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services;

/// <summary>What a cooling channel is, as the user (or FANZi) sees it. Stored per channel in settings.</summary>
public enum CoolingRole
{
    /// <summary>Let FANZi decide from the name, the hardware and how the channel behaves.</summary>
    Auto,
    CpuFan,
    /// <summary>Case / chassis / system fan.</summary>
    SystemFan,
    /// <summary>AIO or custom-loop pump — protected: never driven automatically.</summary>
    Pump,
    GpuFan,
}

/// <summary>How a channel's kind was decided — shown in the UI so a wrong guess is easy to spot and correct.</summary>
public enum CoolingEvidence { Name, Hardware, RpmSignature, UserOverride, Default }

public readonly record struct CoolingClassification(FanDeviceKind Kind, CoolingRole Role, CoolingEvidence Evidence, string Reason)
{
    public bool IsPump => Kind is FanDeviceKind.Pump or FanDeviceKind.AioCooler or FanDeviceKind.IoPump or FanDeviceKind.Watercooler;
}

/// <summary>
/// 4.5: decides whether a channel is a fan or a pump. Before 4.5 this was a loose substring match on the sensor name
/// ("res", "rad", "io", "tuf", "strix" … all meant "liquid"), which both mislabelled ordinary fans and — far worse —
/// missed every pump on a header LibreHardwareMonitor only calls "Fan #2". An unrecognised pump was then treated as the
/// CPU fan and driven by AI Auto-Fan / the fan curve.
///
/// Order of evidence: the user's own choice → hardware type (USB liquid coolers) → name (whole words only) →
/// RPM signature (<see cref="PumpSignatureTracker"/>) → plain fan.
/// </summary>
public static class CoolingChannelClassifier
{
    // Whole-word / token matches only. Each entry is matched against the name split on non-alphanumerics, or as a
    // phrase where it contains a space.
    private static readonly string[] PumpTokens =
    {
        "pump", "wpump", "apump", "aio", "aiopump", "d5", "ddc", "flow", "liquid", "hydro", "waterblock",
        "kraken", "asetek", "ryujin", "ryuo", "eisbaer", "celsius", "liqtech", "coolit", "castle", "galahad",
        "h60", "h80", "h100", "h100i", "h110", "h115", "h115i", "h150", "h150i", "h170", "h170i",
    };

    private static readonly string[] PumpPhrases =
    {
        "w_pump", "a_pump", "aio_pump", "aio pump", "water pump", "pump fan", "liquid cooler", "water cooling", "watercooling",
        "custom loop", "io_pump", "io pump",
    };

    private static readonly string[] CpuTokens = { "cpu", "processor", "cpufan", "cpuopt" };
    private static readonly string[] SystemTokens = { "sys", "system", "chassis", "case", "cha", "sysfan", "chafan", "rear", "front", "top", "bottom", "exhaust", "intake" };

    /// <summary>
    /// Classifies one channel.
    /// </summary>
    /// <param name="name">Sensor / control name as LibreHardwareMonitor reports it.</param>
    /// <param name="hardwareType">LHM hardware type name ("SuperIO", "Cooler", "GpuNvidia" …) — may be empty.</param>
    /// <param name="hardwareName">Hardware name ("NZXT Kraken X63", "Nuvoton NCT6799D" …) — may be empty.</param>
    /// <param name="userRole">The user's saved choice for this channel.</param>
    /// <param name="rpmSaysPump">True when the RPM signature tracker flagged the channel (see <see cref="PumpSignatureTracker"/>).</param>
    public static CoolingClassification Classify(string? name, string? hardwareType, string? hardwareName, CoolingRole userRole = CoolingRole.Auto, bool rpmSaysPump = false)
    {
        string n = name ?? string.Empty;

        if (userRole != CoolingRole.Auto)
        {
            return userRole switch
            {
                CoolingRole.Pump => new(FanDeviceKind.Pump, userRole, CoolingEvidence.UserOverride, "You marked this channel as a pump."),
                _ => new(FanDeviceKind.Fan, userRole, CoolingEvidence.UserOverride, $"You marked this channel as {Label(userRole).ToLowerInvariant()}."),
            };
        }

        bool gpu = hardwareType is not null && hardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);
        if (gpu)
            return new(FanDeviceKind.Fan, CoolingRole.GpuFan, CoolingEvidence.Hardware, "Graphics-card fan.");

        // Name first: "Pump" on a Kraken is a pump, "Fan 1" on the same Kraken is a radiator fan.
        if (NameSaysPump(n))
            return new(FanDeviceKind.Pump, CoolingRole.Pump, CoolingEvidence.Name, $"The name \"{n}\" identifies a pump.");

        // USB liquid coolers (LHM HardwareType.Cooler): a channel that is not explicitly a fan is the pump.
        bool cooler = string.Equals(hardwareType, "Cooler", StringComparison.OrdinalIgnoreCase);
        if (cooler && !Tokens(n).Contains("fan") && HardwareIsLiquidCooler(hardwareName))
            return new(FanDeviceKind.Pump, CoolingRole.Pump, CoolingEvidence.Hardware, $"{hardwareName} is a liquid cooler and this channel is not one of its fans.");

        if (rpmSaysPump)
            return new(FanDeviceKind.Pump, CoolingRole.Pump, CoolingEvidence.RpmSignature,
                "Runs fast and steady whatever the load — that is how a pump behaves. Mark it as a fan below if this is wrong.");

        var tokens = Tokens(n);
        if (tokens.Any(t => CpuTokens.Contains(t)))
            return new(FanDeviceKind.Fan, CoolingRole.CpuFan, CoolingEvidence.Name, "CPU fan header.");
        if (tokens.Any(t => SystemTokens.Contains(t)))
            return new(FanDeviceKind.Fan, CoolingRole.SystemFan, CoolingEvidence.Name, "System / case fan header.");

        return new(FanDeviceKind.Fan, CoolingRole.Auto, CoolingEvidence.Default, "Fan header.");
    }

    /// <summary>True when the name contains a pump word as a whole token or phrase ("Pump", "W_PUMP+", "AIO Pump").</summary>
    public static bool NameSaysPump(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string lower = name.ToLowerInvariant();
        if (PumpPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal))) return true;
        var tokens = Tokens(lower);
        return tokens.Any(t => PumpTokens.Contains(t));
    }

    private static bool HardwareIsLiquidCooler(string? hardwareName)
    {
        if (string.IsNullOrWhiteSpace(hardwareName)) return false;
        string h = hardwareName.ToLowerInvariant();
        string[] marks = { "kraken", "hydro", "liquid", "aio", "asetek", "ryujin", "ryuo", "celsius", "eisbaer", "castle", "galahad", "capellix", "h100", "h115", "h150", "h170", "waterforce", "coreliquid", "mag core" };
        return marks.Any(m => h.Contains(m, StringComparison.Ordinal));
    }

    private static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();
        foreach (char ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) current.Append(ch);
            else if (current.Length > 0) { set.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) set.Add(current.ToString());
        return set;
    }

    public static string Label(CoolingRole role) => role switch
    {
        CoolingRole.CpuFan => "CPU fan",
        CoolingRole.SystemFan => "System fan",
        CoolingRole.Pump => "Pump",
        CoolingRole.GpuFan => "GPU fan",
        _ => "Auto-detect",
    };

    public static CoolingRole ParseRole(string? text) =>
        Enum.TryParse<CoolingRole>(text, ignoreCase: true, out var r) ? r : CoolingRole.Auto;
}

/// <summary>
/// 4.5: recognises a pump on an anonymous header ("Fan #2") from how it behaves. A pump runs fast and almost perfectly
/// steady; fans on the same board idle lower and follow temperature. Pure and deterministic — fed one RPM per poll.
/// </summary>
public sealed class PumpSignatureTracker
{
    /// <summary>Samples needed before a verdict (about 45 s at the 1 s poll).</summary>
    public const int MinSamples = 45;
    public const int Window = 180;
    /// <summary>A pump's mean speed is at least this.</summary>
    public const double MinPumpRpm = 1700;
    /// <summary>Coefficient of variation (σ / mean) a pump stays under.</summary>
    public const double MaxPumpVariation = 0.05;
    /// <summary>A pump spins at least this much faster than the median of the other channels on the same hardware.</summary>
    public const double PeerRatio = 1.45;

    private readonly Dictionary<string, Queue<double>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _latched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records one poll. <paramref name="channels"/>: id, hardware group and RPM of every channel that reports a speed.</summary>
    public void Record(IEnumerable<(string Id, string Group, double? Rpm)> channels)
    {
        var list = channels.ToList();
        foreach (var (id, _, rpm) in list)
        {
            if (rpm is not double r || r <= 0) continue;
            if (!_samples.TryGetValue(id, out var q)) _samples[id] = q = new Queue<double>();
            q.Enqueue(r);
            while (q.Count > Window) q.Dequeue();
        }

        foreach (var group in list.GroupBy(c => c.Group, StringComparer.OrdinalIgnoreCase))
        {
            var ids = group.Select(c => c.Id).ToList();
            foreach (string id in ids)
            {
                if (_latched.Contains(id)) continue;
                if (!_samples.TryGetValue(id, out var q) || q.Count < MinSamples) continue;
                var peers = ids.Where(o => !string.Equals(o, id, StringComparison.OrdinalIgnoreCase))
                    .Select(o => _samples.TryGetValue(o, out var pq) && pq.Count >= MinSamples / 3 ? pq.Average() : (double?)null)
                    .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                if (LooksLikePump(q.ToArray(), peers)) _latched.Add(id);
            }
        }
    }

    /// <summary>Once a channel looked like a pump it stays flagged for the session (a pump never turns into a fan).</summary>
    public bool IsLikelyPump(string id) => _latched.Contains(id);

    /// <summary>The rule itself: fast, steady, and clearly faster than its neighbours (at least one neighbour needed).</summary>
    public static bool LooksLikePump(IReadOnlyList<double> rpm, IReadOnlyList<double> peerMeans)
    {
        if (rpm.Count < MinSamples) return false;
        double mean = rpm.Average();
        if (mean < MinPumpRpm) return false;
        double variance = rpm.Sum(v => (v - mean) * (v - mean)) / rpm.Count;
        if (Math.Sqrt(variance) / mean > MaxPumpVariation) return false;
        if (peerMeans.Count == 0) return false;                 // nothing to compare with: a lone fast fan is not proof
        var sorted = peerMeans.OrderBy(v => v).ToList();
        double median = sorted[sorted.Count / 2];
        return mean >= median * PeerRatio;
    }
}

/// <summary>
/// 4.5 pump protection rules. A pump is not a fan: stalling it cooks the CPU, and running a curve on it only adds noise
/// and wear. FANZi therefore never drives a pump automatically. With protection on (the default) a pump is read-only and
/// stays on its BIOS / firmware setting; with the user's explicit unlock a fixed duty is allowed inside a safe window.
/// </summary>
public static class PumpProtection
{
    /// <summary>Lowest duty FANZi will ever send to a pump (most AIO vendors specify 60 % or more).</summary>
    public const double MinPercent = 60;
    public const double DefaultMaxPercent = 100;

    /// <summary>Clamps a requested pump duty into the safe window [<see cref="MinPercent"/>, <paramref name="maxPercent"/>].</summary>
    public static double Clamp(double requested, double maxPercent = DefaultMaxPercent)
    {
        double max = Math.Clamp(maxPercent, MinPercent, 100);
        return Math.Clamp(requested, MinPercent, max);
    }

    public const string ProtectedMessage =
        "Pump protection — this pump stays on its BIOS / firmware speed. FANZi reads it but never drives it (no AI Auto-Fan, no curve, no profile).";

    public static string UnlockedMessage(double max) =>
        $"Pump unlocked by you — fixed duty only, kept between {MinPercent:F0}% and {Math.Clamp(max, MinPercent, 100):F0}%. Auto hands it back to the BIOS.";
}
