using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// 4.4 AEDi: the Cooling Fingerprint (learns this PC's thermal resistance and forecasts when the cooling needs a
/// clean, see <see cref="CoolingFingerprint"/>) and the Self-Doctor auto-debugger (<see cref="SelfDoctor"/>).
/// </summary>
public partial class MainWindowViewModel
{
    private CoolingFingerprint? _cooling;
    private SelfDoctor? _doctor;
    private IRgbService? _rgbServiceForDoctor;
    private HardwareSnapshot? _lastSnapshotForDoctor;
    private DateTime _lastCoolingSaveUtc = DateTime.UtcNow;
    private DateTime _lastCoolingUiUtc = DateTime.MinValue;
    private static readonly TimeSpan CoolingSaveInterval = TimeSpan.FromMinutes(10);

    // ── Cooling Fingerprint (dashboard card) ────────────────────────────────
    [ObservableProperty] private string _coolingHeadline = "Learning your cooling (0%)";
    [ObservableProperty] private string _coolingDetail = "AEDi fingerprints the cooler while you use the PC normally.";
    [ObservableProperty] private string _coolingEfficiency = "--";
    [ObservableProperty] private double _coolingEfficiencyValue;
    [ObservableProperty] private string _coolingResistance = "--";
    [ObservableProperty] private string _coolingForecast = "--";
    [ObservableProperty] private string _coolingStateColor = "#60A5FA";
    [ObservableProperty] private string _coolingStateLabel = "LEARNING";

    // ── Self-Doctor (dashboard card) ───────────────────────────────────────────
    public ObservableCollection<DoctorCheck> DoctorChecks { get; } = new();
    [ObservableProperty] private string _doctorSummary = "Self-Doctor starts 20 s after launch.";
    [ObservableProperty] private string _doctorFixesLabel = "0 auto-fixes";
    [ObservableProperty] private bool _isDoctorRunning;

    private IAsyncRelayCommand? _runDoctorCommand;
    public IAsyncRelayCommand RunDoctorCommand => _runDoctorCommand ??= new AsyncRelayCommand(RunDoctorNowAsync, () => !IsDoctorRunning);

    private IAsyncRelayCommand? _saveDiagnosticsCommand;
    public IAsyncRelayCommand SaveDiagnosticsCommand => _saveDiagnosticsCommand ??= new AsyncRelayCommand(SaveDiagnosticsAsync);

    private IRelayCommand? _relearnCoolingCommand;
    /// <summary>New cooler / case / repaste: forget the fingerprint and learn again.</summary>
    public IRelayCommand RelearnCoolingCommand => _relearnCoolingCommand ??= new RelayCommand(() =>
    {
        _cooling?.Relearn();
        SaveCooling();
        ApplyCoolingReport(_cooling?.LastReport);
        StatusMessage = "AEDi Cooling Fingerprint: learning this cooler from scratch.";
        AppLog.Info("AEDi Cooling Fingerprint: relearn requested by the user.");
    });

    /// <summary>Called once from the constructor (the RGB service is needed for the MAXSUN checks).</summary>
    private void InitializeAedi(IRgbService rgbService)
    {
        _rgbServiceForDoctor = rgbService;
        _cooling = CoolingFingerprint.Load(CoolingFingerprint.DefaultPath, DateTimeOffset.Now);
        _cooling.StateWorsened += report =>
            RaiseAlert(AppAlertKind.Anomaly, report.State >= CoolingState.CleanSoon ? AppAlertLevel.Warning : AppAlertLevel.Information,
                "cooling-fingerprint", "AEDi Cooling Fingerprint: " + report.Headline, report.Detail);
        ApplyCoolingReport(_cooling.LastReport);

        _doctor = new SelfDoctor(new SelfDoctorContext
        {
            LastSnapshot = () => _lastSnapshotForDoctor,
            SensorInitError = () => _hardwareMonitorService.InitializationError,
            RecoverSensors = () => _hardwareMonitorService.TryRecoverAsync(),
            RgbWanted = () => RgbControl.RgbEnabled,
            RgbConnected = () => _rgbServiceForDoctor?.IsConnected == true,
            RepairRgb = () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => RgbControl.ConnectCommand.ExecuteAsync(null)),
            MaxsunOnline = () => _rgbServiceForDoctor is MaxsunSyncRgbService { IsMaxsunOnline: true },
            MaxsunServiceState = () => OperatingSystem.IsWindows() && _rgbServiceForDoctor is MaxsunSyncRgbService ? WindowsServiceState("MaxsunSyncService") : null,
            StartMaxsunService = () => StartWindowsService("MaxsunSyncService"),
            WorkingSetBytes = () => { using var p = Process.GetCurrentProcess(); return p.WorkingSet64; },
            UiResponds = async timeout =>
            {
                var op = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Input);
                return await Task.WhenAny(op.GetTask(), Task.Delay(timeout)).ConfigureAwait(false) == op.GetTask();
            },
            ExtraReport = () => _cooling?.LastReport is { } r ? $"Cooling Fingerprint: {r.State} — {r.Headline}. {r.Detail}" : null,
        });
        _doctor.Completed += checks => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyDoctorChecks(checks));
        _doctor.NeedsAttention += (title, message) => RaiseAlert(AppAlertKind.Anomaly, AppAlertLevel.Warning, "self-doctor:" + title, title, message);
        _doctor.Start();
    }

    /// <summary>Called from RefreshAsync with every snapshot (UI thread).</summary>
    private void FeedAedi(HardwareSnapshot snapshot)
    {
        _lastSnapshotForDoctor = snapshot;
        if (_cooling is null) return;
        try
        {
            if (_cooling.Record(snapshot) || DateTime.UtcNow - _lastCoolingUiUtc > TimeSpan.FromSeconds(30))
            {
                _lastCoolingUiUtc = DateTime.UtcNow;
                ApplyCoolingReport(_cooling.LastReport.State == CoolingState.Learning ? _cooling.LastReport : _cooling.Evaluate(snapshot.Timestamp));
            }
            if (DateTime.UtcNow - _lastCoolingSaveUtc > CoolingSaveInterval) SaveCooling();
        }
        catch (Exception ex)
        {
            AppLog.Warn("AEDi Cooling Fingerprint: update failed", ex);
        }
    }

    private void ApplyCoolingReport(CoolingReport? r)
    {
        if (r is null) return;
        CoolingHeadline = r.Headline;
        CoolingDetail = r.Detail;
        CoolingEfficiencyValue = r.EfficiencyPercent ?? r.LearningProgress * 100;
        CoolingEfficiency = r.EfficiencyPercent is double e ? $"{e:F0}%" : $"{r.LearningProgress * 100:F0}% learned";
        CoolingResistance = r.CurrentKPerWatt is double k ? $"{k.ToString("F3", CultureInfo.CurrentCulture)} K/W" : "--";
        CoolingForecast = r.DaysUntilCleaning switch
        {
            null when r.State == CoolingState.Learning => "learning",
            null => r.TrendPercentPerWeek is double t ? $"{t:+0.0;-0.0}%/wk" : "needs a week",
            <= 0.5 => "now",
            double d => $"~{d:F0} days",
        };
        (CoolingStateLabel, CoolingStateColor) = r.State switch
        {
            CoolingState.Learning => ("LEARNING", "#60A5FA"),
            CoolingState.Healthy => ("HEALTHY", "#34D399"),
            CoolingState.Watch => ("WATCH", "#FBBF24"),
            CoolingState.CleanSoon => ("CLEAN SOON", "#F97316"),
            _ => ("DEGRADED", "#EF4444"),
        };
    }

    private void SaveCooling()
    {
        _lastCoolingSaveUtc = DateTime.UtcNow;
        try { _cooling?.Save(CoolingFingerprint.DefaultPath); }
        catch (Exception ex) { AppLog.Warn("AEDi Cooling Fingerprint: save failed", ex); }
    }

    private void ApplyDoctorChecks(System.Collections.Generic.IReadOnlyList<DoctorCheck> checks)
    {
        SyncInPlace(DoctorChecks, checks);
        int attention = 0;
        foreach (var c in checks) if (c.Status == DoctorStatus.Attention) attention++;
        int fixes = _doctor?.FixesApplied ?? 0;
        DoctorFixesLabel = fixes == 1 ? "1 auto-fix" : $"{fixes} auto-fixes";
        DoctorSummary = attention == 0
            ? $"All {checks.Count} checks healthy · {DateTime.Now:HH:mm:ss}"
            : $"{attention} item(s) need attention · {DateTime.Now:HH:mm:ss}";
    }

    private async Task RunDoctorNowAsync()
    {
        if (_doctor is null) return;
        IsDoctorRunning = true;
        RunDoctorCommand.NotifyCanExecuteChanged();
        try { ApplyDoctorChecks(await _doctor.RunAsync()); }
        finally
        {
            IsDoctorRunning = false;
            RunDoctorCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task SaveDiagnosticsAsync()
    {
        if (_doctor is null) return;
        try
        {
            string path = await Task.Run(() => _doctor.SaveDiagnosticsBundle("saved by the user"));
            StatusMessage = $"Diagnostics saved: {path}";
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Saving diagnostics failed", ex);
            StatusMessage = $"Saving diagnostics failed: {ex.Message}";
        }
    }

    private void DisposeAedi()
    {
        SaveCooling();
        _doctor?.Dispose();
        _doctor = null;
    }

    /// <summary>"RUNNING", "STOPPED", … from <c>sc query</c>; null when the service is not installed.</summary>
    internal static string? WindowsServiceState(string service)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", $"query \"{service}\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return ParseScState(output);
        }
        catch
        {
            return null;
        }
    }

    internal static string? ParseScState(string scOutput)
    {
        var m = System.Text.RegularExpressions.Regex.Match(scOutput, @"STATE\s*:\s*\d+\s+([A-Z_]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool StartWindowsService(string service)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", $"start \"{service}\"") { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(10000);
            for (int i = 0; i < 10; i++)
            {
                if (WindowsServiceState(service) == "RUNNING") return true;
                System.Threading.Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Starting {service} failed", ex);
        }
        return false;
    }
}
