using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.AI;

/// <summary>Where the cooling stands compared with the day FANZi learned it.</summary>
public enum CoolingState { Learning, Healthy, Watch, CleanSoon, Degraded }

/// <summary>One fan that now spins slower at the same duty than when it was learned.</summary>
public sealed record FanWearWarning(string FanId, string FanName, int DutyBucket, double BaselineRpm, double CurrentRpm)
{
    public double DropPercent => BaselineRpm <= 0 ? 0 : (1 - CurrentRpm / BaselineRpm) * 100;
}

/// <summary>Snapshot of the fingerprint for the UI / alerts.</summary>
public sealed record CoolingReport(
    CoolingState State,
    double? EfficiencyPercent,
    double? CurrentKPerWatt,
    double? BaselineKPerWatt,
    double? TrendPercentPerWeek,
    double? DaysUntilCleaning,
    IReadOnlyList<FanWearWarning> FanWarnings,
    int Observations,
    double LearningProgress,
    string Headline,
    string Detail);

/// <summary>
/// 4.4 AEDi Cooling Fingerprint — learns this PC's thermal DNA and forecasts when the cooling needs attention.
///
/// Every 5 minutes of real use FANZi fits CPU temperature against CPU package power
/// (<c>T = T₀ + R·P</c>). The slope <c>R</c> is the cooler's thermal resistance in K/W: how many degrees one extra watt
/// costs. Ambient temperature only moves <c>T₀</c>, so <c>R</c> is ambient-independent, and it is kept per CPU-fan speed
/// band so a BIOS fan curve ramping up does not look like better cooling. The first observations in a band become its
/// baseline ("as new"); later ones give the efficiency <c>baseline ÷ R</c>. Dust, drying thermal paste, a clogged
/// filter or an AIO losing fluid all raise <c>R</c> slowly — the daily trend is regressed into a forecast:
/// "clean in ~N days". Fan tachometers are fingerprinted too: RPM per 10 % duty bucket; a fan that loses ≥ 15 % RPM at
/// the same duty points to bearing wear or an obstruction.
///
/// Everything is local: <c>%APPDATA%\FANZI\ai\cooling-fingerprint.json</c>. Pure logic — the caller supplies time.
/// </summary>
public sealed class CoolingFingerprint
{
    // ── Tunables ──────────────────────────────────────────────────────────────
    internal static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(5);
    internal const int MinWindowSamples = 60;
    internal const double MinPowerSpreadWatts = 12;
    internal const double MinFitR2 = 0.45;
    internal const int BaselineObservations = 8;
    internal const int RpmBandWidth = 300;
    internal const double CleanSoonEfficiency = 88;
    internal const double DegradedEfficiency = 80;
    internal const double WatchEfficiency = 94;
    internal const int MaxObservations = 3000;
    internal const int FanBaselineSamples = 40;
    internal const int FanRecentSamples = 60;
    internal const double FanWearDropPercent = 15;

    private readonly List<(DateTimeOffset T, double Temp, double Power, double? Rpm)> _window = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private FingerprintData _data = new();
    private readonly Dictionary<string, Dictionary<int, Queue<double>>> _fanRecent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fanNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (caller's thread) when the state gets worse, e.g. Healthy → CleanSoon, or a fan starts wearing.</summary>
    public event Action<CoolingReport>? StateWorsened;

    public CoolingReport LastReport { get; private set; } = Learning(0, 0);

    /// <summary>Feeds one hardware snapshot (≈ 1 Hz). Returns true when a 5-minute window was closed and evaluated.</summary>
    public bool Record(HardwareSnapshot s) =>
        Record(s.Timestamp, s.CpuPackageTemperature ?? s.CpuAverageTemperature, s.CpuPackagePowerWatts, s.CpuFan?.SpeedRpm, s.Fans);

    public bool Record(DateTimeOffset now, double? cpuTemp, double? cpuPowerW, double? cpuFanRpm, IReadOnlyList<FanChannelSnapshot>? fans = null)
    {
        if (fans is not null) RecordFans(fans);

        bool evaluated = false;
        if (cpuTemp is double t && cpuPowerW is double p && double.IsFinite(t) && double.IsFinite(p) && t is > 5 and < 120 && p is > 0.5 and < 600)
        {
            if (_window.Count == 0) _windowStart = now;
            _window.Add((now, t, p, cpuFanRpm));
        }

        if (_window.Count > 0 && now - _windowStart >= WindowLength)
        {
            CloseWindow(now);
            evaluated = true;
        }
        return evaluated;
    }

    /// <summary>Forget everything and learn again (new cooler, new case, repaste).</summary>
    public void Relearn()
    {
        _data = new FingerprintData();
        _window.Clear();
        _fanRecent.Clear();
        LastReport = Learning(0, 0);
    }

    public CoolingReport Evaluate(DateTimeOffset now)
    {
        var previous = LastReport;
        var report = BuildReport(now);
        LastReport = report;
        if (Rank(report.State) > Rank(previous.State) && previous.State != CoolingState.Learning
            || report.FanWarnings.Count > previous.FanWarnings.Count)
        {
            try { StateWorsened?.Invoke(report); } catch { /* UI handler errors never break learning */ }
        }
        return report;
    }

    // ── Thermal resistance ────────────────────────────────────────────────────

    private void CloseWindow(DateTimeOffset now)
    {
        var w = _window.ToArray();
        _window.Clear();
        if (w.Length < MinWindowSamples) return;

        double pMin = w.Min(x => x.Power), pMax = w.Max(x => x.Power);
        if (pMax - pMin < MinPowerSpreadWatts) return; // idle or steady load: slope is not observable

        if (!TryFit(w.Select(x => x.Power).ToArray(), w.Select(x => x.Temp).ToArray(), out double slope, out double r2)) return;
        if (r2 < MinFitR2 || slope <= 0.005 || slope > 5) return;

        var rpms = w.Where(x => x.Rpm is > 0).Select(x => x.Rpm!.Value).ToArray();
        int band = rpms.Length == 0 ? -1 : (int)(Median(rpms) / RpmBandWidth);

        _data.Observations.Add(new Observation { TimeUtc = now.UtcDateTime, Band = band, KPerW = slope });
        if (_data.Observations.Count > MaxObservations)
            _data.Observations.RemoveRange(0, _data.Observations.Count - MaxObservations);

        string key = band.ToString(CultureInfo.InvariantCulture);
        if (!_data.Baselines.ContainsKey(key))
        {
            var inBand = _data.Observations.Where(o => o.Band == band).Select(o => o.KPerW).ToArray();
            if (inBand.Length >= BaselineObservations) _data.Baselines[key] = Median(inBand);
        }
        _data.FirstLearnedUtc ??= now.UtcDateTime;
        Evaluate(now);
    }

    /// <summary>Ordinary least squares y = a + b·x. False when x has no variance.</summary>
    internal static bool TryFit(double[] x, double[] y, out double slope, out double r2)
    {
        slope = 0; r2 = 0;
        int n = x.Length;
        if (n < 3 || y.Length != n) return false;
        double mx = x.Average(), my = y.Average();
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        if (sxx <= 1e-9) return false;
        slope = sxy / sxx;
        r2 = syy <= 1e-9 ? 0 : sxy * sxy / (sxx * syy);
        return true;
    }

    private CoolingReport BuildReport(DateTimeOffset now)
    {
        var fanWarnings = FanWarnings();
        var scored = _data.Observations
            .Select(o => (o, b: _data.Baselines.TryGetValue(o.Band.ToString(CultureInfo.InvariantCulture), out double b) ? b : (double?)null))
            .Where(x => x.b is not null)
            .Select(x => (x.o.TimeUtc, Eff: Math.Clamp(x.b!.Value / x.o.KPerW * 100, 20, 160), x.o.KPerW, Base: x.b!.Value))
            .OrderBy(x => x.TimeUtc)
            .ToList();

        int n = _data.Observations.Count;
        if (_data.Baselines.Count == 0 || scored.Count == 0)
        {
            double progress = Math.Clamp(n / (double)BaselineObservations, 0, 0.99);
            var learning = Learning(n, progress);
            return fanWarnings.Count == 0 ? learning : learning with { FanWarnings = fanWarnings, Detail = learning.Detail + " " + FanText(fanWarnings) };
        }

        // Current efficiency: median of the last 12 observations (~1 h of varied load).
        var recent = scored.TakeLast(12).ToList();
        double eff = Median(recent.Select(x => x.Eff).ToArray());
        double kpw = Median(recent.Select(x => x.KPerW).ToArray());
        double basek = Median(recent.Select(x => x.Base).ToArray());

        // Trend: daily mean efficiency over the last 45 days, regressed against days.
        var daily = scored.Where(x => x.TimeUtc >= now.UtcDateTime.AddDays(-45))
            .GroupBy(x => x.TimeUtc.Date)
            .Select(g => (Day: (g.Key - DateTime.UnixEpoch).TotalDays, Eff: g.Average(x => x.Eff)))
            .OrderBy(x => x.Day).ToArray();
        double? perWeek = null, daysToClean = null;
        if (daily.Length >= 5 && daily[^1].Day - daily[0].Day >= 6
            && TryFit(daily.Select(d => d.Day).ToArray(), daily.Select(d => d.Eff).ToArray(), out double perDay, out _))
        {
            perWeek = perDay * 7;
            if (perDay < -0.02 && eff > CleanSoonEfficiency)
                daysToClean = Math.Min(365, (eff - CleanSoonEfficiency) / -perDay);
            else if (eff <= CleanSoonEfficiency)
                daysToClean = 0;
        }

        CoolingState state = eff <= DegradedEfficiency ? CoolingState.Degraded
            : eff <= CleanSoonEfficiency || daysToClean is <= 14 ? CoolingState.CleanSoon
            : eff <= WatchEfficiency || perWeek is < -0.75 ? CoolingState.Watch
            : CoolingState.Healthy;

        string headline = state switch
        {
            CoolingState.Healthy => $"Cooling at {eff:F0}% of new",
            CoolingState.Watch => $"Cooling at {eff:F0}% — slowly losing efficiency",
            CoolingState.CleanSoon => daysToClean is > 0.5 ? $"Clean the cooling in ~{daysToClean:F0} days" : "Clean the cooling soon",
            _ => $"Cooling degraded to {eff:F0}% of new",
        };
        string detail = $"Thermal resistance {kpw:F3} K/W (baseline {basek:F3} K/W): every extra CPU watt now costs {kpw:F2} °C" +
                        (perWeek is double pw ? $"; trend {pw:+0.0;-0.0}% per week." : ".") +
                        state switch
                        {
                            CoolingState.CleanSoon or CoolingState.Degraded => " Blow out the CPU cooler fins and dust filters; if that does not bring it back, re-paste the CPU or check the AIO pump.",
                            CoolingState.Watch => " Nothing urgent — AEDi keeps watching.",
                            _ => "",
                        } +
                        (fanWarnings.Count > 0 ? " " + FanText(fanWarnings) : "");

        if (fanWarnings.Count > 0 && state < CoolingState.Watch) state = CoolingState.Watch;
        return new CoolingReport(state, eff, kpw, basek, perWeek, daysToClean, fanWarnings, n, 1, headline, detail);
    }

    private static CoolingReport Learning(int n, double progress) => new(
        CoolingState.Learning, null, null, null, null, null, Array.Empty<FanWearWarning>(), n, progress,
        $"Learning your cooling ({progress * 100:F0}%)",
        "AEDi fingerprints the cooler while you use the PC normally — it needs varied CPU load (about an hour of real use) to learn the baseline.");

    private static string FanText(IReadOnlyList<FanWearWarning> w) =>
        string.Join(" ", w.Select(f => $"{f.FanName} spins {f.DropPercent:F0}% slower at {f.DutyBucket * 10}% duty than when learned ({f.CurrentRpm:F0} vs {f.BaselineRpm:F0} RPM) — check for bearing wear or a blockage."));

    private static int Rank(CoolingState s) => s switch
    {
        CoolingState.Learning => 0, CoolingState.Healthy => 1, CoolingState.Watch => 2, CoolingState.CleanSoon => 3, _ => 4,
    };

    // ── Fan wear ──────────────────────────────────────────────────────────────

    private void RecordFans(IReadOnlyList<FanChannelSnapshot> fans)
    {
        foreach (var f in fans)
        {
            if (f.SpeedRpm is not double rpm || rpm < 150 || f.CurrentControlPercent is not double duty || duty < 15 || duty > 100) continue;
            int bucket = (int)Math.Round(duty / 10);
            _fanNames[f.Id] = f.Name;

            if (!_data.FanBaselines.TryGetValue(f.Id, out var buckets)) _data.FanBaselines[f.Id] = buckets = new Dictionary<string, FanBucket>();
            string key = bucket.ToString(CultureInfo.InvariantCulture);
            if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = new FanBucket();
            if (b.Samples.Count < FanBaselineSamples)
            {
                b.Samples.Add(rpm);
                continue;
            }

            if (!_fanRecent.TryGetValue(f.Id, out var recent)) _fanRecent[f.Id] = recent = new Dictionary<int, Queue<double>>();
            if (!recent.TryGetValue(bucket, out var q)) recent[bucket] = q = new Queue<double>();
            q.Enqueue(rpm);
            while (q.Count > FanRecentSamples) q.Dequeue();
        }
    }

    private List<FanWearWarning> FanWarnings()
    {
        var list = new List<FanWearWarning>();
        foreach (var (fanId, recent) in _fanRecent)
        {
            if (!_data.FanBaselines.TryGetValue(fanId, out var buckets)) continue;
            foreach (var (bucket, q) in recent)
            {
                if (q.Count < FanRecentSamples / 2) continue;
                if (!buckets.TryGetValue(bucket.ToString(CultureInfo.InvariantCulture), out var b) || b.Samples.Count < FanBaselineSamples) continue;
                double baseRpm = Median(b.Samples.ToArray()), now = Median(q.ToArray());
                if (baseRpm > 0 && (1 - now / baseRpm) * 100 >= FanWearDropPercent)
                    list.Add(new FanWearWarning(fanId, _fanNames.GetValueOrDefault(fanId, fanId), bucket, baseRpm, now));
            }
        }
        return list.GroupBy(w => w.FanId).Select(g => g.OrderByDescending(w => w.DropPercent).First()).ToList();
    }

    internal static double Median(double[] v)
    {
        if (v.Length == 0) return double.NaN;
        var s = (double[])v.Clone();
        Array.Sort(s);
        int m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FANZI", "ai", "cooling-fingerprint.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Loads a saved fingerprint; a missing or damaged file starts fresh (never throws).</summary>
    public static CoolingFingerprint Load(string path, DateTimeOffset now)
    {
        var fp = new CoolingFingerprint();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<FingerprintData>(File.ReadAllText(path), Json) is { } data && data.Version == FingerprintData.CurrentVersion)
            {
                fp._data = data;
                fp.LastReport = fp.BuildReport(now);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Services.AppLog.Warn("AEDi Cooling Fingerprint: saved fingerprint unreadable — learning again", ex);
        }
        return fp;
    }

    internal sealed class FingerprintData
    {
        public const int CurrentVersion = 1;
        public int Version { get; set; } = CurrentVersion;
        public DateTime? FirstLearnedUtc { get; set; }
        public List<Observation> Observations { get; set; } = new();
        public Dictionary<string, double> Baselines { get; set; } = new();
        public Dictionary<string, Dictionary<string, FanBucket>> FanBaselines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class Observation
    {
        public DateTime TimeUtc { get; set; }
        public int Band { get; set; }
        public double KPerW { get; set; }
    }

    internal sealed class FanBucket
    {
        public List<double> Samples { get; set; } = new();
    }
}
