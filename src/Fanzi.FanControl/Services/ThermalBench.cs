using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>One sample recorded during a bench run (1 Hz).</summary>
public readonly record struct BenchSample(double T, double? CpuTempC, double? GpuTempC, double? CpuLoadPct, double? FanRpm, double? CpuWatts);

/// <summary>Phase of a run.</summary>
public enum BenchPhase { Idle, Baseline, Load, Cooldown, Done }

/// <summary>
/// Everything a finished run tells you about the cooling (pure, computed from the samples — unit-tested).
/// </summary>
public sealed record BenchResult(
    DateTime StartedLocal,
    int LoadSeconds,
    int Threads,
    double? IdleTempC,
    double? PeakTempC,
    double? RiseC,
    double? RiseRateCPerSec,          // slope over the first 10 s of load
    double? TimeToPeakSec,
    double? SteadyStdDevC,            // temperature wobble over the last third of the load phase
    double? IdleFanRpm,
    double? PeakFanRpm,
    double? FanLagSec,                // seconds from load start until the fan gained 15 % of its eventual rise
    double? RecoverySec,              // seconds after load end until within 3 °C of idle (null = never within cooldown)
    double? AvgCpuWatts,
    double? CoolingPerWatt,           // °C rise per 100 W — lower is better
    int Score,                        // 0–100
    string Grade,                     // A+ … E
    string Verdict)
{
    public string Summary =>
        $"{Grade} ({Score}/100) · idle {Fmt(IdleTempC)} → peak {Fmt(PeakTempC)} (+{Fmt(RiseC, "0.0")}) · " +
        $"fan {Fmt(IdleFanRpm, "0")} → {Fmt(PeakFanRpm, "0")} rpm · recovery {Fmt(RecoverySec, "0")} s";

    private static string Fmt(double? v, string f = "0.0") => v is double d ? d.ToString(f, System.Globalization.CultureInfo.InvariantCulture) : "—";
}

/// <summary>
/// 4.3 Thermal Bench — a repeatable cooling benchmark. Pins every core at 100 % for a fixed time while sampling
/// CPU / GPU temperature, fan speed and power from FANZi's own monitor, then scores how the cooling coped: how
/// far and how fast the temperature rose, how quickly the fans answered, how stable the plateau was and how fast
/// it recovered. Run it before and after a fan-curve change (or a repaste) and compare.
/// </summary>
public static class ThermalBench
{
    /// <summary>Maximum temperature a run is allowed to reach before it aborts itself.</summary>
    public const double SafetyLimitC = 95;

    /// <summary>Burns CPU on <paramref name="threads"/> threads until the token is cancelled. Returns iterations done.</summary>
    public static Task<long> BurnAsync(int threads, CancellationToken token)
    {
        threads = Math.Clamp(threads, 1, Environment.ProcessorCount * 2);
        return Task.Run(() =>
        {
            long total = 0;
            var workers = new Thread[threads];
            var counts = new long[threads];
            for (int i = 0; i < threads; i++)
            {
                int w = i;
                workers[i] = new Thread(() =>
                {
                    // Mixed integer + floating-point + memory work so every execution unit is busy, like a real
                    // all-core render or compile. The sink prevents the JIT from optimising the loop away.
                    double acc = 1.0001;
                    ulong h = 0x9E3779B97F4A7C15UL;
                    var block = new double[4096];
                    long n = 0;
                    while (!token.IsCancellationRequested)
                    {
                        for (int k = 0; k < 4096; k++)
                        {
                            acc = Math.Sqrt(acc * 1.0000001 + 0.5) + Math.Sin(acc) * 0.001;
                            h ^= h << 13; h ^= h >> 7; h ^= h << 17;
                            block[(int)(h & 4095)] = acc + block[k];
                        }
                        n++;
                    }
                    counts[w] = n;
                    if (acc == 42 && h == 7) Console.WriteLine(block[0]); // never true — keeps the work alive
                })
                { IsBackground = true, Name = $"FANZi bench {w}", Priority = ThreadPriority.BelowNormal };
                workers[i].Start();
            }
            foreach (var t in workers) t.Join();
            foreach (var c in counts) total += c;
            return total;
        }, CancellationToken.None);
    }

    /// <summary>Scores a run from its samples. <paramref name="loadStartT"/> / <paramref name="loadEndT"/> are in seconds (sample T).</summary>
    public static BenchResult Analyse(IReadOnlyList<BenchSample> samples, double loadStartT, double loadEndT, int threads, DateTime startedLocal)
    {
        var baseline = samples.Where(s => s.T < loadStartT).ToList();
        var load = samples.Where(s => s.T >= loadStartT && s.T <= loadEndT).ToList();
        var cool = samples.Where(s => s.T > loadEndT).ToList();

        double? idleTemp = Avg(baseline.Select(s => s.CpuTempC)) ?? load.FirstOrDefault().CpuTempC;
        double? peak = load.Select(s => s.CpuTempC).Where(v => v.HasValue).DefaultIfEmpty(null).Max();
        double? rise = peak - idleTemp;

        double? riseRate = null;
        var first10 = load.Where(s => s.T <= loadStartT + 10 && s.CpuTempC.HasValue).ToList();
        if (first10.Count >= 3 && idleTemp.HasValue)
        {
            var last = first10[^1];
            riseRate = (last.CpuTempC!.Value - idleTemp.Value) / Math.Max(1, last.T - loadStartT);
        }

        double? timeToPeak = null;
        if (peak.HasValue)
        {
            var at = load.FirstOrDefault(s => s.CpuTempC.HasValue && s.CpuTempC.Value >= peak.Value - 0.5);
            if (at.CpuTempC.HasValue) timeToPeak = at.T - loadStartT;
        }

        double? steady = null;
        int third = load.Count / 3;
        var tail = load.Skip(load.Count - third).Select(s => s.CpuTempC).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (tail.Count >= 3) steady = StdDev(tail);

        double? idleFan = Avg(baseline.Select(s => s.FanRpm)) ?? load.FirstOrDefault().FanRpm;
        double? peakFan = load.Select(s => s.FanRpm).Where(v => v.HasValue).DefaultIfEmpty(null).Max();
        double? fanLag = null;
        if (idleFan.HasValue && peakFan.HasValue && peakFan > idleFan + 50)
        {
            double threshold = idleFan.Value + (peakFan.Value - idleFan.Value) * 0.15;
            var at = load.FirstOrDefault(s => s.FanRpm.HasValue && s.FanRpm.Value >= threshold);
            if (at.FanRpm.HasValue) fanLag = at.T - loadStartT;
        }

        double? recovery = null;
        if (idleTemp.HasValue)
        {
            var at = cool.FirstOrDefault(s => s.CpuTempC.HasValue && s.CpuTempC.Value <= idleTemp.Value + 3);
            if (at.CpuTempC.HasValue) recovery = at.T - loadEndT;
        }

        double? watts = Avg(load.Select(s => s.CpuWatts));
        double? perWatt = watts is > 10 && rise.HasValue ? rise / watts * 100.0 : null;

        int score = Score(rise, riseRate, steady, fanLag, recovery, peak);
        string grade = score >= 90 ? "A+" : score >= 80 ? "A" : score >= 70 ? "B" : score >= 55 ? "C" : score >= 40 ? "D" : "E";
        string verdict = Verdict(score, rise, peak, fanLag, recovery, steady);

        return new BenchResult(startedLocal, (int)Math.Round(loadEndT - loadStartT), threads, idleTemp, peak, rise, riseRate, timeToPeak,
            steady, idleFan, peakFan, fanLag, recovery, watts, perWatt, score, grade, verdict);
    }

    /// <summary>0–100: 40 pts rise, 15 ramp speed, 15 plateau stability, 15 fan response, 15 recovery (+ peak penalty).</summary>
    internal static int Score(double? rise, double? riseRate, double? steady, double? fanLag, double? recovery, double? peak)
    {
        double s = 0;
        s += rise is double r ? 40 * Math.Clamp(1 - (r - 8) / 32.0, 0, 1) : 20;          // +8 °C full marks … +40 °C zero
        s += riseRate is double rr ? 15 * Math.Clamp(1 - (rr - 0.3) / 2.2, 0, 1) : 7;     // 0.3 °C/s fine … 2.5 °C/s zero
        s += steady is double sd ? 15 * Math.Clamp(1 - (sd - 0.5) / 3.0, 0, 1) : 7;       // ±0.5 °C flat … ±3.5 °C zero
        s += fanLag is double fl ? 15 * Math.Clamp(1 - (fl - 2) / 18.0, 0, 1) : 7;        // 2 s instant … 20 s zero
        s += recovery is double rc ? 15 * Math.Clamp(1 - (rc - 10) / 50.0, 0, 1) : 5;     // 10 s … 60 s zero (null = didn't recover)
        if (peak is double p && p >= 90) s -= 15;                                          // throttling territory
        else if (peak is double p2 && p2 >= 85) s -= 7;
        return (int)Math.Round(Math.Clamp(s, 0, 100));
    }

    internal static string Verdict(int score, double? rise, double? peak, double? fanLag, double? recovery, double? steady)
    {
        var parts = new List<string>();
        parts.Add(score >= 80 ? "Cooling has headroom." : score >= 55 ? "Cooling is adequate." : "Cooling is struggling.");
        if (peak is >= 90) parts.Add("The CPU reached throttling temperatures — raise the fan curve or check the mount / paste.");
        else if (peak is >= 85) parts.Add("Peak is high; a steeper curve above 70 °C would buy margin.");
        if (fanLag is > 8) parts.Add($"Fans took {fanLag:0} s to react — lower the curve's hysteresis or start ramping earlier.");
        if (steady is > 2.5) parts.Add("The plateau oscillates; the curve is probably hunting between two steps — smooth it.");
        if (recovery is null) parts.Add("Temperature did not return to idle during cooldown — case airflow may be the limit.");
        else if (recovery is > 45) parts.Add("Slow recovery suggests heat-soaked case air; more exhaust helps.");
        if (rise is <= 15 && score >= 80) parts.Add("You could run quieter: try a gentler curve and bench again.");
        return string.Join(" ", parts);
    }

    private static double? Avg(IEnumerable<double?> xs)
    {
        var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : v.Average();
    }

    private static double StdDev(IReadOnlyList<double> v)
    {
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count);
    }

    /// <summary>CSV export of a run (header + one row per sample) for spreadsheets.</summary>
    public static string ToCsv(IReadOnlyList<BenchSample> samples, BenchResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# FANZi IO-nity Thermal Bench — " + result.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("# " + result.Summary);
        sb.AppendLine("t_s,cpu_temp_c,gpu_temp_c,cpu_load_pct,fan_rpm,cpu_watts");
        foreach (var s in samples)
            sb.AppendLine(string.Join(",", s.T.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                F(s.CpuTempC), F(s.GpuTempC), F(s.CpuLoadPct), F(s.FanRpm), F(s.CpuWatts)));
        return sb.ToString();

        static string F(double? v) => v is double d ? d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "";
    }
}
