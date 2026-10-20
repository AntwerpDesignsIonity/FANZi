using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

/// <summary>4.3 Thermal Bench: scoring is pure and reflects what the cooling did.</summary>
public class ThermalBenchTests
{
    /// <summary>Synthetic run: idle 40 °C, exponential approach to <paramref name="plateau"/> during load, decay after.</summary>
    private static List<BenchSample> Synth(double plateau, double fanIdle, double fanPeak, double fanLagSec, double tau = 15, int load = 60, int cool = 60, double? wobble = null)
    {
        var s = new List<BenchSample>();
        for (int t = 0; t < 10; t++) s.Add(new BenchSample(t, 40, 35, 3, fanIdle, 30));
        double temp = 40;
        for (int t = 10; t < 10 + load; t++)
        {
            double k = t - 10;
            temp = 40 + (plateau - 40) * (1 - Math.Exp(-k / tau)) + (wobble is double w ? Math.Sin(k) * w : 0);
            double fan = k < fanLagSec ? fanIdle : fanIdle + (fanPeak - fanIdle) * Math.Min(1, (k - fanLagSec) / 10);
            s.Add(new BenchSample(t, temp, 40, 100, fan, 140));
        }
        double end = temp;
        for (int t = 10 + load; t < 10 + load + cool; t++)
        {
            double k = t - 10 - load;
            s.Add(new BenchSample(t, 40 + (end - 40) * Math.Exp(-k / 12), 36, 4, fanPeak, 30));
        }
        return s;
    }

    [Fact]
    public void Good_cooling_scores_high_and_bad_cooling_scores_low()
    {
        var good = ThermalBench.Analyse(Synth(plateau: 58, fanIdle: 900, fanPeak: 1600, fanLagSec: 2), 10, 70, 16, DateTime.Now);
        var bad = ThermalBench.Analyse(Synth(plateau: 94, fanIdle: 900, fanPeak: 1100, fanLagSec: 20, tau: 6, wobble: 3), 10, 70, 16, DateTime.Now);

        Assert.True(good.Score > bad.Score, $"good {good.Score} should beat bad {bad.Score}");
        Assert.True(good.Score >= 75, $"good run scored {good.Score}");
        Assert.True(bad.Score <= 40, $"bad run scored {bad.Score}");
        Assert.True(good.Grade[0] is 'A' or 'B', good.Grade);
        Assert.Contains("throttling", bad.Verdict);
        Assert.Contains("Fans took", bad.Verdict);
    }

    [Fact]
    public void Metrics_are_read_from_the_samples()
    {
        var r = ThermalBench.Analyse(Synth(plateau: 70, fanIdle: 800, fanPeak: 1500, fanLagSec: 4), 10, 70, 8, new DateTime(2026, 10, 20, 7, 0, 0));
        Assert.Equal(40, r.IdleTempC!.Value, 1);
        Assert.InRange(r.PeakTempC!.Value, 68, 70.1);
        Assert.InRange(r.RiseC!.Value, 28, 30.1);
        Assert.InRange(r.FanLagSec!.Value, 4, 7);
        Assert.Equal(1500, r.PeakFanRpm!.Value, 1);
        Assert.NotNull(r.RecoverySec);
        Assert.True(r.RecoverySec > 0);
        Assert.NotNull(r.RiseRateCPerSec);
        Assert.Equal(60, r.LoadSeconds);
        Assert.Equal(8, r.Threads);
        Assert.Contains("idle 40.0", r.Summary);
        Assert.Contains("°C per 100 W", "" + (r.CoolingPerWatt is not null ? "°C per 100 W" : ""));
    }

    [Fact]
    public void Missing_sensors_still_produce_a_result()
    {
        var samples = Enumerable.Range(0, 30).Select(t => new BenchSample(t, null, null, 100, null, null)).ToList();
        var r = ThermalBench.Analyse(samples, 5, 25, 4, DateTime.Now);
        Assert.Null(r.PeakTempC);
        Assert.InRange(r.Score, 0, 100);
        Assert.False(string.IsNullOrEmpty(r.Verdict));
    }

    [Fact]
    public void Csv_has_header_and_one_row_per_sample()
    {
        var samples = Synth(60, 900, 1400, 3);
        var r = ThermalBench.Analyse(samples, 10, 70, 16, DateTime.Now);
        string csv = ThermalBench.ToCsv(samples, r);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("# FANZi IO-nity Thermal Bench", lines[0]);
        Assert.Equal("t_s,cpu_temp_c,gpu_temp_c,cpu_load_pct,fan_rpm,cpu_watts", lines[2].Trim());
        Assert.Equal(samples.Count, lines.Length - 3);
    }

    [Fact]
    public void Compare_line_shows_score_delta()
    {
        var a = ThermalBench.Analyse(Synth(70, 900, 1400, 3), 10, 70, 16, DateTime.Now);
        var b = ThermalBench.Analyse(Synth(60, 900, 1600, 2), 10, 70, 16, DateTime.Now);
        string line = ThermalBenchViewModel.Compare(a, b);
        Assert.Contains($"score {a.Score} → {b.Score}", line);
        Assert.Contains("rise", line);
    }

    [Fact]
    public async Task Burn_runs_until_cancelled_and_does_work()
    {
        using var cts = new CancellationTokenSource(400);
        long iterations = await ThermalBench.BurnAsync(2, cts.Token);
        Assert.True(iterations > 0);
    }

    [Fact]
    public void Score_penalises_throttling_peaks()
    {
        int cool = ThermalBench.Score(rise: 20, riseRate: 1, steady: 1, fanLag: 5, recovery: 20, peak: 70);
        int hot = ThermalBench.Score(rise: 20, riseRate: 1, steady: 1, fanLag: 5, recovery: 20, peak: 92);
        Assert.Equal(15, cool - hot);
    }
}
