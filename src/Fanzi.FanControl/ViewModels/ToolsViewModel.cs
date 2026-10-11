using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// v4.1.0 "Tools" tab: Disk Optimizer (analyse / optimise / defragment / ReTrim per volume), Drive Health
/// (S.M.A.R.T.), System Repair (restore point, SFC, DISM, component clean-up, CHKDSK scan), Network fixes, Quick
/// fixes and Power &amp; Gaming (power plans incl. Ultimate Performance, Game Mode, GPU scheduling, hibernation).
/// One tool runs at a time; its real console output streams into the built-in console with live progress.
/// </summary>
public sealed partial class ToolsViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogLines = 600;

    private readonly ToolProcessRunner _runner;
    private readonly DiskToolsService _disks;
    private CancellationTokenSource? _runCts;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loaded;

    public ToolsViewModel() : this(new ToolProcessRunner(), new DiskToolsService()) { }

    internal ToolsViewModel(ToolProcessRunner runner, DiskToolsService disks)
    {
        _runner = runner;
        _disks = disks;
        IsAdmin = DetectAdmin();
        foreach (var action in SystemToolsService.Catalogue)
        {
            (action.Group switch { "Repair" => RepairActions, "Network" => NetworkActions, _ => FixActions }).Add(action);
        }
    }

    // ── Shared console ────────────────────────────────────────────────────────

    public bool IsAdmin { get; }
    public bool ShowAdminWarning => !IsAdmin;

    public ObservableCollection<string> LogLines { get; } = new();
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _consoleTitle = "Console";
    [ObservableProperty] private string _consoleStatus = "Pick a tool — its real output appears here.";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private bool _lastResultOk = true;
    [ObservableProperty] private bool _restartRequired;

    /// <summary>A disruptive action waiting for the user's OK (inline banner).</summary>
    [ObservableProperty] private ToolAction? _pendingConfirmation;
    public bool HasPendingConfirmation => PendingConfirmation is not null;
    partial void OnPendingConfirmationChanged(ToolAction? value) => OnPropertyChanged(nameof(HasPendingConfirmation));

    partial void OnIsRunningChanged(bool value)
    {
        CancelCommand.NotifyCanExecuteChanged();
        foreach (var v in Volumes) v.NotifyBusy();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    private void Log(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    /// <summary>Runs <paramref name="command"/> in the console (one at a time); returns null when another tool is running.</summary>
    internal async Task<ToolRunResult?> RunToolAsync(ToolCommand command)
    {
        if (IsRunning) return null;
        IsRunning = true;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ConsoleTitle = command.Title;
        ConsoleStatus = "Starting…";
        ProgressPercent = 0;
        ProgressIndeterminate = true;
        LastResult = "";
        Log("");
        Log($"▶ {command.Title}   ({command})   {DateTime.Now:HH:mm:ss}");

        string? lastProgressLine = null;
        var progress = new Progress<ToolProgress>(p =>
        {
            if (p.Percent is double pct)
            {
                ProgressIndeterminate = false;
                ProgressPercent = pct;
            }
            if (!string.IsNullOrWhiteSpace(p.Line))
            {
                ConsoleStatus = p.Line!.Trim();
                if (!p.IsProgressLine || p.Percent is null)
                {
                    if (!p.IsProgressLine) Log("  " + p.Line);
                }
                lastProgressLine = p.IsProgressLine ? p.Line : null;
            }
        });

        try
        {
            var result = await _runner.RunAsync(command, progress, _runCts.Token);
            await Task.Yield(); // let queued progress callbacks land before the summary
            if (lastProgressLine is { } tail && !LogLines.LastOrDefault("").Contains(tail.Trim(), StringComparison.Ordinal))
                Log("  " + tail.Trim());

            ProgressIndeterminate = false;
            if (result.Succeeded) ProgressPercent = 100;
            LastResultOk = result.Succeeded;
            LastResult = result.Cancelled
                ? $"⏹ {command.Title} cancelled after {result.Elapsed.TotalSeconds:F0} s."
                : result.Succeeded
                    ? $"✔ {command.Title} finished in {Format(result.Elapsed)}." + (command.NeedsRestart ? " Restart Windows to finish." : "")
                    : $"✖ {command.Title} failed: {result.ErrorSummary}" + (IsAdmin ? "" : " (FANZi is not running as Administrator.)");
            ConsoleStatus = LastResult;
            Log(LastResult);
            if (result.Succeeded && command.NeedsRestart) RestartRequired = true;
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Tool {command} failed", ex);
            LastResultOk = false;
            LastResult = $"✖ {command.Title}: {ex.Message}";
            Log(LastResult);
            return null;
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            IsRunning = false;
        }
    }

    private static string Format(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.TotalSeconds:F1} s";

    // ── Catalogue actions (repair / network / fixes) ──────────────────────────

    public ObservableCollection<ToolAction> RepairActions { get; } = new();
    public ObservableCollection<ToolAction> NetworkActions { get; } = new();
    public ObservableCollection<ToolAction> FixActions { get; } = new();

    [RelayCommand]
    private async Task RunAction(ToolAction? action)
    {
        if (action is null || IsRunning) return;
        if (action.Confirm is not null)
        {
            PendingConfirmation = action;
            return;
        }
        await RunToolAsync(action.Command);
    }

    [RelayCommand]
    private async Task ConfirmPending()
    {
        var action = PendingConfirmation;
        PendingConfirmation = null;
        if (action is not null) await RunToolAsync(action.Command);
    }

    [RelayCommand]
    private void CancelPending() => PendingConfirmation = null;

    // ── Disk optimizer ────────────────────────────────────────────────────────

    public ObservableCollection<VolumeItemViewModel> Volumes { get; } = new();
    [ObservableProperty] private string _diskStatus = "Loading drives…";

    [RelayCommand]
    private async Task RefreshDrives()
    {
        DiskStatus = "Reading drives…";
        try
        {
            var volumes = await _disks.ListVolumesAsync(_lifetime.Token);
            var previous = Volumes.ToDictionary(v => v.Volume.Letter);
            Volumes.Clear();
            foreach (var v in volumes)
            {
                var item = new VolumeItemViewModel(v, this);
                if (previous.TryGetValue(v.Letter, out var old)) item.CopyStatusFrom(old);
                Volumes.Add(item);
            }
            int ssd = volumes.Count(v => v.Media == DiskMedia.Ssd), hdd = volumes.Count(v => v.Media == DiskMedia.Hdd);
            DiskStatus = volumes.Count == 0
                ? "No fixed drives found."
                : $"{volumes.Count} drive(s): {ssd} SSD, {hdd} HDD. Optimise picks the right method per drive — TRIM for SSDs, defragmentation for HDDs.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("Listing drives failed", ex);
            DiskStatus = "Could not read the drives: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task OptimizeAll()
    {
        var result = await RunToolAsync(new ToolCommand("Optimise all drives", "defrag.exe", "/C /O /U /V"));
        if (result is { Succeeded: true })
            foreach (var v in Volumes) v.Status = $"Optimised {DateTime.Now:HH:mm}";
    }

    internal async Task RunDiskAsync(VolumeItemViewModel item, DiskOperation operation)
    {
        var command = DiskToolsService.CommandFor(operation, item.Volume.Letter);
        item.Status = operation == DiskOperation.Analyze ? "Analysing…" : "Working…";
        var result = await RunToolAsync(command);
        if (result is null) { item.Status = "Another tool is running"; return; }
        if (!result.Succeeded)
        {
            item.Status = result.Cancelled ? "Cancelled" : "Failed — see console";
            return;
        }

        var report = DiskToolsService.ParseReport(result.Output);
        if (report.FragmentedPercent is double f) item.Fragmentation = $"{f:F0} % fragmented";
        item.Status = operation switch
        {
            DiskOperation.Analyze => report.Recommendation ?? "Analysed",
            DiskOperation.Retrim => $"ReTrimmed {DateTime.Now:HH:mm}",
            DiskOperation.Defragment => $"Defragmented {DateTime.Now:HH:mm}",
            _ => $"Optimised {DateTime.Now:HH:mm}",
        };
        await RefreshDrives();
    }

    // ── Drive health ──────────────────────────────────────────────────────────

    public ObservableCollection<DriveHealthItem> DriveHealth { get; } = new();
    [ObservableProperty] private string _healthStatus = "Loading drive health…";

    [RelayCommand]
    private async Task RefreshHealth()
    {
        HealthStatus = "Reading S.M.A.R.T. counters…";
        try
        {
            var drives = await _disks.ListDriveHealthAsync(_lifetime.Token);
            DriveHealth.Clear();
            foreach (var d in drives) DriveHealth.Add(new DriveHealthItem(d));
            int bad = drives.Count(d => d.Verdict != "Healthy");
            HealthStatus = drives.Count == 0 ? "No physical disks reported."
                : bad == 0 ? $"All {drives.Count} drive(s) healthy."
                : $"{bad} of {drives.Count} drive(s) need attention.";
            if (drives.Any(d => d.Note is not null))
                HealthStatus += " " + drives.First(d => d.Note is not null).Note;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("Reading drive health failed", ex);
            HealthStatus = "Could not read drive health: " + ex.Message;
        }
    }

    // ── Power & gaming ────────────────────────────────────────────────────────

    public ObservableCollection<PowerPlan> PowerPlans { get; } = new();
    [ObservableProperty] private PowerPlan? _selectedPlan;
    [ObservableProperty] private string _powerStatus = "";
    [ObservableProperty] private bool _hasUltimate;
    [ObservableProperty] private bool _gameModeOn;
    [ObservableProperty] private bool _hagsOn;
    [ObservableProperty] private string _hagsStatus = "";
    [ObservableProperty] private bool _hibernationOn;
    [ObservableProperty] private string _hibernationStatus = "";
    private bool _suppressToggles;

    [RelayCommand]
    private async Task RefreshPower()
    {
        try
        {
            var plans = await SystemToolsService.ListPowerPlansAsync(_runner, _lifetime.Token);
            PowerPlans.Clear();
            foreach (var p in plans) PowerPlans.Add(p);
            SelectedPlan = plans.FirstOrDefault(p => p.IsActive);
            HasUltimate = SystemToolsService.HasUltimate(plans);
            PowerStatus = SelectedPlan is { } active ? $"Active plan: {active.Name}" : "No active plan reported.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("Reading power plans failed", ex);
            PowerStatus = "Could not read power plans: " + ex.Message;
        }
        ReadToggles();
    }

    private void ReadToggles()
    {
        if (!OperatingSystem.IsWindows()) return;
        _suppressToggles = true;
        try
        {
            GameModeOn = SystemToolsService.IsGameModeOn();
            bool? hags = SystemToolsService.IsHagsOn();
            HagsOn = hags == true;
            HagsStatus = hags is null ? "Driver default (not set)" : hags.Value ? "On" : "Off";
            long? hiber = SystemToolsService.HiberfilBytes();
            HibernationOn = hiber is not null;
            HibernationStatus = hiber is long b ? $"On — hiberfil.sys uses {b / 1024.0 / 1024 / 1024:F1} GB" : "Off";
        }
        finally { _suppressToggles = false; }
    }

    [RelayCommand]
    private async Task ActivatePlan()
    {
        if (SelectedPlan is not { } plan) return;
        var result = await RunToolAsync(SystemToolsService.SetActiveCommand(plan.Id));
        if (result is not null) await RefreshPower();
    }

    [RelayCommand]
    private async Task AddUltimate()
    {
        var result = await RunToolAsync(SystemToolsService.AddUltimateCommand);
        if (result is not null) await RefreshPower();
    }

    partial void OnGameModeOnChanged(bool value)
    {
        if (_suppressToggles || !OperatingSystem.IsWindows()) return;
        try
        {
            SystemToolsService.SetGameMode(value);
            Log($"Game Mode {(value ? "on" : "off")}.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Setting Game Mode failed", ex);
            LastResultOk = false;
            LastResult = "✖ Game Mode: " + ex.Message;
            ReadToggles();
        }
    }

    partial void OnHagsOnChanged(bool value)
    {
        if (_suppressToggles || !OperatingSystem.IsWindows()) return;
        try
        {
            SystemToolsService.SetHags(value);
            HagsStatus = (value ? "On" : "Off") + " — restart Windows to apply";
            RestartRequired = true;
            Log($"Hardware-accelerated GPU scheduling {(value ? "on" : "off")} (after restart).");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Setting GPU scheduling failed", ex);
            LastResultOk = false;
            LastResult = "✖ GPU scheduling needs Administrator: " + ex.Message;
            ReadToggles();
        }
    }

    partial void OnHibernationOnChanged(bool value)
    {
        if (_suppressToggles) return;
        _ = SetHibernationAsync(value);
    }

    private async Task SetHibernationAsync(bool on)
    {
        var result = await RunToolAsync(SystemToolsService.HibernationCommand(on));
        if (result is null)
        {
            LastResultOk = false;
            LastResult = "Another tool is running — try again when it finishes.";
        }
        ReadToggles();
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    /// <summary>Loads drives, health and power state the first time the tab is shown.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshDrives();
        await RefreshPower();
        await RefreshHealth();
    }

    private static bool DetectAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { }
        _runCts?.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>One volume row of the Disk Optimizer.</summary>
public sealed partial class VolumeItemViewModel : ObservableObject
{
    private readonly ToolsViewModel _owner;

    public VolumeItemViewModel(DiskVolume volume, ToolsViewModel owner)
    {
        Volume = volume;
        _owner = owner;
    }

    public DiskVolume Volume { get; }
    public string Title => string.IsNullOrWhiteSpace(Volume.Label) ? $"{Volume.Root}" : $"{Volume.Root}  {Volume.Label}";
    public string Detail => $"{Volume.MediaText}{(Volume.Bus.Length > 0 ? " · " + Volume.Bus : "")} · {Volume.FileSystem} · " +
                            $"{Gb(Volume.FreeBytes)} free of {Gb(Volume.SizeBytes)}" + (Volume.DiskName.Length > 0 ? $" · {Volume.DiskName}" : "");
    public double UsedPercent => Volume.UsedPercent;
    public string MediaText => Volume.MediaText;
    public bool CanDefragment => Volume.CanDefragment;
    public bool CanRetrim => Volume.CanRetrim;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _fragmentation = "";

    public bool IsIdle => !_owner.IsRunning;
    internal void NotifyBusy() => OnPropertyChanged(nameof(IsIdle));

    internal void CopyStatusFrom(VolumeItemViewModel other)
    {
        Status = other.Status;
        Fragmentation = other.Fragmentation;
    }

    [RelayCommand] private Task Analyze() => _owner.RunDiskAsync(this, DiskOperation.Analyze);
    [RelayCommand] private Task Optimize() => _owner.RunDiskAsync(this, DiskOperation.Optimize);
    [RelayCommand] private Task Defragment() => _owner.RunDiskAsync(this, DiskOperation.Defragment);
    [RelayCommand] private Task Retrim() => _owner.RunDiskAsync(this, DiskOperation.Retrim);

    private static string Gb(long bytes) => bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):F2} TB" : $"{bytes / (double)(1L << 30):F0} GB";
}

/// <summary>Display wrapper for one disk's health.</summary>
public sealed class DriveHealthItem
{
    public DriveHealthItem(DriveHealth health) => Health = health;
    public DriveHealth Health { get; }
    public string Name => Health.Name;
    public string Kind => $"{(Health.Media == DiskMedia.Ssd ? "SSD" : Health.Media == DiskMedia.Hdd ? "HDD" : "Disk")} · {Health.Bus} · {Health.SizeBytes / 1e9:F0} GB";
    public string Verdict => Health.Verdict;
    public bool IsGood => Health.Verdict == "Healthy";
    public string Temperature => Health.TemperatureC is double t ? $"{t:F0} °C" + (Health.TemperatureMaxC is double m ? $" (max {m:F0})" : "") : "—";
    public string Wear => Health.WearPercent is double w ? $"{w:F0} % used" : "—";
    public string Hours => Health.PowerOnHours is long h ? $"{h:N0} h" : "—";
    public string Errors => Health.ReadErrors is null && Health.WriteErrors is null ? "—" : $"{Health.ReadErrors ?? 0} / {Health.WriteErrors ?? 0}";
}
