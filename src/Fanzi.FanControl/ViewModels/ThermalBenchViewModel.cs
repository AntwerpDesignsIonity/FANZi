using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>Live readings the bench samples — supplied by MainWindowViewModel from the hardware poll.</summary>
public readonly record struct BenchReadings(double? CpuTempC, double? GpuTempC, double? CpuLoadPct, double? FanRpm, double? CpuWatts);

/// <summary>A finished run in the history list.</summary>
public sealed record BenchHistoryRow(BenchResult Result)
{
    public string When => Result.StartedLocal.ToString("HH:mm:ss");
    public string Grade => Result.Grade;
    public int Score => Result.Score;
    public string Duration => $"{Result.LoadSeconds} s · {Result.Threads} thr";
    public string Idle => Result.IdleTempC is double v ? $"{v:0.0} °C" : "—";
    public string Peak => Result.PeakTempC is double v ? $"{v:0.0} °C" : "—";
    public string Rise => Result.RiseC is double v ? $"+{v:0.0}" : "—";
    public string Fan => Result.PeakFanRpm is double v ? $"{v:0} rpm" : "—";
    public string Recovery => Result.RecoverySec is double v ? $"{v:0} s" : "—";
}

/// <summary>
/// 4.3 Thermal Bench tab: baseline → all-core load → cooldown, sampled at 1 Hz from FANZi's own monitor, scored by
/// <see cref="ThermalBench"/>. Compare runs to see what a fan-curve change actually did.
/// </summary>
public sealed partial class ThermalBenchViewModel : ViewModelBase, IDisposable
{
    private readonly List<BenchSample> _samples = new();
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _runCts;
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>Set by MainWindowViewModel: the latest hardware readings.</summary>
    public Func<BenchReadings>? ReadingsProvider { get; set; }

    public ObservableCollection<BenchHistoryRow> History { get; } = new();

    /// <summary>Points for the live temperature chart (x = seconds, y = °C), consumed by a Polyline.</summary>
    public AvaloniaList<Point> TempPoints { get; } = new();
    public AvaloniaList<Point> FanPoints { get; } = new();

    [ObservableProperty] private int _loadSeconds = 60;
    [ObservableProperty] private int _threads = Environment.ProcessorCount;
    [ObservableProperty] private int _cooldownSeconds = 60;
    [ObservableProperty] private int _baselineSeconds = 10;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PhaseLabel), nameof(IsRunning), nameof(StartButtonText))]
    private BenchPhase _phase = BenchPhase.Idle;

    [ObservableProperty] private string _status = "Ready. Close heavy apps, then press Start — the bench loads every core for the chosen time and watches how your cooling answers.";
    [ObservableProperty] private double _progress;          // 0..1 over the whole run
    [ObservableProperty] private string _elapsedLabel = "0 s";
    [ObservableProperty] private string _liveTemp = "—";
    [ObservableProperty] private string _liveFan = "—";
    [ObservableProperty] private string _liveLoad = "—";
    [ObservableProperty] private string _liveWatts = "—";
    [ObservableProperty] private string _liveRise = "—";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResult))] private BenchResult? _lastResult;
    [ObservableProperty] private string _resultGrade = "—";
    [ObservableProperty] private string _resultGradeColor = "#6E8DB5";
    [ObservableProperty] private string _resultHeadline = "No run yet";
    [ObservableProperty] private string _resultVerdict = "";
    [ObservableProperty] private string _resultDetails = "";
    [ObservableProperty] private string _compareLine = "";
    [ObservableProperty] private string _chartMaxLabel = "100 °C";
    [ObservableProperty] private string _chartMinLabel = "20 °C";

    public bool HasResult => LastResult is not null;
    public bool IsRunning => Phase is BenchPhase.Baseline or BenchPhase.Load or BenchPhase.Cooldown;
    public string StartButtonText => IsRunning ? "■ Stop" : "▶ Start bench";
    public string PhaseLabel => Phase switch
    {
        BenchPhase.Baseline => "BASELINE",
        BenchPhase.Load => "LOAD — ALL CORES",
        BenchPhase.Cooldown => "COOLDOWN",
        BenchPhase.Done => "DONE",
        _ => "IDLE",
    };

    public IReadOnlyList<int> DurationChoices { get; } = [30, 60, 120, 300];
    public IReadOnlyList<int> ThreadChoices { get; } = BuildThreadChoices();

    public IAsyncRelayCommand StartStopCommand { get; }
    public IAsyncRelayCommand ExportCsvCommand { get; }
    public IRelayCommand ClearHistoryCommand { get; }

    private const int ChartWidth = 600, ChartHeight = 160;

    public ThermalBenchViewModel()
    {
        StartStopCommand = new AsyncRelayCommand(StartStopAsync);
        ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, () => HasResult);
        ClearHistoryCommand = new RelayCommand(() => History.Clear());
    }

    private static IReadOnlyList<int> BuildThreadChoices()
    {
        int n = Environment.ProcessorCount;
        var list = new SortedSet<int> { n, Math.Max(1, n / 2), Math.Max(1, n / 4), 1 };
        return list.ToArray();
    }

    private async Task StartStopAsync()
    {
        if (IsRunning) { _runCts?.Cancel(); return; }
        if (ReadingsProvider is null) { Status = "Hardware monitor is not ready yet."; return; }

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var token = _runCts.Token;
        _samples.Clear();
        TempPoints.Clear();
        FanPoints.Clear();
        _clock.Restart();
        var started = DateTime.Now;
        int baseline = Math.Clamp(BaselineSeconds, 3, 30), load = Math.Clamp(LoadSeconds, 10, 900), cool = Math.Clamp(CooldownSeconds, 10, 300);
        double total = baseline + load + cool;
        double? idle = null;
        bool aborted = false;

        try
        {
            Phase = BenchPhase.Baseline;
            Status = $"Baseline: {baseline} s of idle readings…";
            await SampleForAsync(baseline, total, token, () => idle = CurrentIdle());
            idle = CurrentIdle();

            Phase = BenchPhase.Load;
            Status = $"Loading {Threads} thread(s) for {load} s — watch the fans answer. Stops itself at {ThermalBench.SafetyLimitC:0} °C.";
            AppLog.Info($"Thermal bench: load {load} s × {Threads} threads");
            using var burnCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var burn = ThermalBench.BurnAsync(Threads, burnCts.Token);
            double loadStart = _clock.Elapsed.TotalSeconds;
            try
            {
                await SampleForAsync(load, total, token, () =>
                {
                    var last = _samples[^1];
                    if (last.CpuTempC is double t && t >= ThermalBench.SafetyLimitC)
                    {
                        aborted = true;
                        Status = $"Safety stop at {t:0.0} °C — the CPU reached FANZi's bench limit.";
                        burnCts.Cancel();
                    }
                }, () => aborted);
            }
            finally
            {
                burnCts.Cancel();
                try { await burn; } catch { }
            }
            double loadEnd = _clock.Elapsed.TotalSeconds;

            Phase = BenchPhase.Cooldown;
            Status = aborted ? Status + " Cooling down…" : $"Cooldown: {cool} s — measuring how fast the temperature comes back.";
            // Ends early once we're back near idle for 5 consecutive seconds.
            await SampleForAsync(cool, total, token, stopWhen: () =>
                idle is double i && _samples.Count >= 5 && _samples.TakeLast(5).All(s => s.CpuTempC is double t && t <= i + 2));

            Finish(started, loadStart, loadEnd);
        }
        catch (OperationCanceledException)
        {
            if (_disposeCts.IsCancellationRequested) return;
            Phase = BenchPhase.Idle;
            Status = "Bench stopped.";
            Progress = 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Thermal bench failed", ex);
            Phase = BenchPhase.Idle;
            Status = $"Bench failed: {ex.Message}";
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private double? CurrentIdle()
    {
        var v = _samples.Select(s => s.CpuTempC).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : v.Average();
    }

    /// <summary>Samples once a second for <paramref name="seconds"/>; <paramref name="afterSample"/> runs after each sample.</summary>
    private async Task SampleForAsync(int seconds, double total, CancellationToken token, Action? afterSample = null, Func<bool>? stopWhen = null)
    {
        double end = _clock.Elapsed.TotalSeconds + seconds;
        while (_clock.Elapsed.TotalSeconds < end)
        {
            token.ThrowIfCancellationRequested();
            var r = ReadingsProvider!();
            double t = Math.Round(_clock.Elapsed.TotalSeconds);
            _samples.Add(new BenchSample(t, r.CpuTempC, r.GpuTempC, r.CpuLoadPct, r.FanRpm, r.CpuWatts));
            UpdateLive(r, t, total);
            afterSample?.Invoke();
            if (stopWhen?.Invoke() == true) return;
            await Task.Delay(1000, token);
        }
    }

    private void UpdateLive(BenchReadings r, double t, double total)
    {
        ElapsedLabel = $"{t:0} s";
        Progress = Math.Clamp(t / total, 0, 1);
        LiveTemp = r.CpuTempC is double c ? $"{c:0.0} °C" : "—";
        LiveFan = r.FanRpm is double f ? $"{f:0} rpm" : "—";
        LiveLoad = r.CpuLoadPct is double l ? $"{l:0} %" : "—";
        LiveWatts = r.CpuWatts is double w ? $"{w:0} W" : "—";
        var idle = _samples.Where(s => s.T < BaselineSeconds).Select(s => s.CpuTempC).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        LiveRise = idle.Count > 0 && r.CpuTempC is double cc ? $"{cc - idle.Average():+0.0;-0.0} °C" : "—";
        RebuildChart(total);
    }

    private void RebuildChart(double total)
    {
        var temps = _samples.Where(s => s.CpuTempC.HasValue).ToList();
        double lo = 20, hi = 100;
        if (temps.Count > 0)
        {
            lo = Math.Floor((temps.Min(s => s.CpuTempC!.Value) - 5) / 10) * 10;
            hi = Math.Ceiling((temps.Max(s => s.CpuTempC!.Value) + 5) / 10) * 10;
            if (hi - lo < 20) hi = lo + 20;
        }
        ChartMinLabel = $"{lo:0} °C";
        ChartMaxLabel = $"{hi:0} °C";
        double maxFan = Math.Max(1000, _samples.Where(s => s.FanRpm.HasValue).Select(s => s.FanRpm!.Value).DefaultIfEmpty(1000).Max() * 1.1);

        TempPoints.Clear();
        FanPoints.Clear();
        foreach (var s in _samples)
        {
            double x = s.T / Math.Max(total, 1) * ChartWidth;
            if (s.CpuTempC is double c) TempPoints.Add(new Point(x, ChartHeight - (c - lo) / (hi - lo) * ChartHeight));
            if (s.FanRpm is double f) FanPoints.Add(new Point(x, ChartHeight - f / maxFan * ChartHeight));
        }
    }

    private void Finish(DateTime started, double loadStart, double loadEnd)
    {
        var result = ThermalBench.Analyse(_samples, loadStart, loadEnd, Threads, started);
        var previous = LastResult;
        LastResult = result;
        History.Insert(0, new BenchHistoryRow(result));
        while (History.Count > 20) History.RemoveAt(History.Count - 1);

        ResultGrade = result.Grade;
        ResultGradeColor = result.Score >= 80 ? "#22C55E" : result.Score >= 55 ? "#FBBF24" : "#EF4444";
        ResultHeadline = result.Summary;
        ResultVerdict = result.Verdict;
        ResultDetails =
            $"Rise rate {Fmt(result.RiseRateCPerSec, "0.00")} °C/s · time to peak {Fmt(result.TimeToPeakSec, "0")} s · plateau ±{Fmt(result.SteadyStdDevC)} °C · " +
            $"fan lag {Fmt(result.FanLagSec, "0")} s · avg CPU power {Fmt(result.AvgCpuWatts, "0")} W" +
            (result.CoolingPerWatt is double cpw ? $" · {cpw:0.0} °C per 100 W" : "");
        CompareLine = previous is null ? "" : Compare(previous, result);
        Phase = BenchPhase.Done;
        Progress = 1;
        Status = "Run complete. Change the fan curve, then bench again and compare.";
        ExportCsvCommand.NotifyCanExecuteChanged();
        AppLog.Info("Thermal bench: " + result.Summary);
    }

    internal static string Compare(BenchResult a, BenchResult b)
    {
        var parts = new List<string> { $"vs previous: score {a.Score} → {b.Score}" };
        if (a.RiseC is double ar && b.RiseC is double br) parts.Add($"rise {ar:0.0} → {br:0.0} °C ({br - ar:+0.0;-0.0})");
        if (a.PeakFanRpm is double af && b.PeakFanRpm is double bf) parts.Add($"peak fan {af:0} → {bf:0} rpm");
        if (a.RecoverySec is double arc && b.RecoverySec is double brc) parts.Add($"recovery {arc:0} → {brc:0} s");
        return string.Join(" · ", parts);
    }

    private static string Fmt(double? v, string f = "0.0") => v is double d ? d.ToString(f, System.Globalization.CultureInfo.InvariantCulture) : "—";

    private async Task ExportCsvAsync()
    {
        if (LastResult is null) return;
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FANZi", "Thermal Bench");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"bench-{LastResult.StartedLocal:yyyyMMdd-HHmmss}.csv");
            await File.WriteAllTextAsync(path, ThermalBench.ToCsv(_samples, LastResult), _disposeCts.Token);
            Status = $"Exported to {path}";
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}
