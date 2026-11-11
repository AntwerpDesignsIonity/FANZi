using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>One chip of the dashboard cooling strip: "SYS 2 · 870 RPM".</summary>
public sealed record CoolingChip(string Label, string Value, string Color, string Tip);

/// <summary>
/// 4.5 cooling: pump protection, per-channel roles (fan / pump / system fan), the system-fan group, "apply all",
/// and the dashboard strip that shows every fan's speed — not only the CPU fan.
/// </summary>
public partial class MainWindowViewModel
{
    // ── Pump protection ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PumpProtectionStatus))]
    private bool _pumpProtectionEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PumpProtectionStatus))]
    private double _pumpMaxPercent = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PumpProtectionStatus))]
    private string _pumpSummary = "No pump detected yet — FANZi watches every channel; a pump shows up here within a minute.";

    [ObservableProperty] private bool _hasPump;

    public string PumpProtectionStatus => PumpProtectionEnabled
        ? "On — a pump is never driven by FANZi: no AI Auto-Fan, no curve, no profile. It stays on its BIOS / firmware speed. " + PumpSummary
        : $"Off — you may set a fixed pump duty between {PumpProtection.MinPercent:F0}% and {PumpMaxPercent:F0}%. Automatic control still never touches a pump. " + PumpSummary;

    partial void OnPumpProtectionEnabledChanged(bool value)
    {
        _hardwareMonitorService.PumpProtectionEnabled = value;
        if (_suppressProfileSync) return;
        _appSettings.PumpProtectionEnabled = value;
        _ = SaveSettingsAsync();
        StatusMessage = value
            ? "Pump protection on: pumps are back on their BIOS / firmware speed."
            : "Pump protection off: fixed pump duty is allowed inside the safe window.";
        _ = SafeExecuteAsync(RefreshAsync, "Refresh after pump protection change");
    }

    partial void OnPumpMaxPercentChanged(double value)
    {
        double clamped = Math.Clamp(value, PumpProtection.MinPercent, 100);
        _hardwareMonitorService.PumpMaxPercent = clamped;
        if (_suppressProfileSync) return;
        _appSettings.PumpMaxPercent = clamped;
        _ = SaveSettingsAsync();
    }

    // ── Roles ────────────────────────────────────────────────────────────────

    private void PushChannelRoles()
    {
        var roles = new Dictionary<string, CoolingRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, text) in _appSettings.FanChannelRoles)
        {
            var role = CoolingChannelClassifier.ParseRole(text);
            if (role != CoolingRole.Auto) roles[id] = role;
        }
        _hardwareMonitorService.SetChannelRoles(roles);
    }

    /// <summary>Called by a fan row when the user picks "Pump" / "System fan" / … for it.</summary>
    private void OnChannelRoleChosen(FanChannelViewModel channel, CoolingRole role)
    {
        if (role == CoolingRole.Auto) _appSettings.FanChannelRoles.Remove(channel.Id);
        else _appSettings.FanChannelRoles[channel.Id] = role.ToString();
        PushChannelRoles();
        _ = SaveSettingsAsync();
        StatusMessage = role == CoolingRole.Auto
            ? $"{channel.Name}: back to auto-detect."
            : $"{channel.Name} is now treated as {CoolingChannelClassifier.Label(role).ToLowerInvariant()}.";
        AppLog.Info($"Fans: user set '{channel.Name}' ({channel.Id}) to {role}.");
        _ = SafeExecuteAsync(RefreshAsync, "Refresh after channel role change");
    }

    // ── System fans group + apply all ────────────────────────────────────────

    [ObservableProperty] private double _systemFansPercent = 50;
    [ObservableProperty] private string _systemFansSummary = "No system fans detected";
    [ObservableProperty] private bool _hasSystemFans;
    [ObservableProperty] private bool _canControlSystemFans;
    [ObservableProperty] private string _systemFansHint = "";
    [ObservableProperty] private bool _hasPendingFanChanges;
    [ObservableProperty] private string _fanLockSummary = "";
    [ObservableProperty] private bool _hasFanLockSummary;

    /// <summary>Dashboard strip: every channel with its speed.</summary>
    public ObservableCollection<CoolingChip> CoolingChips { get; } = new();

    /// <summary>HUD tile next to "CPU FAN": average speed of the system fans ("—" when none report).</summary>
    [ObservableProperty] private string _systemFansHud = "—";

    private IAsyncRelayCommand? _applySystemFansCommand;
    public IAsyncRelayCommand ApplySystemFansCommand => _applySystemFansCommand ??=
        new AsyncRelayCommand(() => SafeExecuteAsync(ApplySystemFansAsync, "Apply system fans"));

    private IAsyncRelayCommand? _applyAllFansCommand;
    /// <summary>Applies every row's target in one go (pumps and BIOS-owned headers are skipped by the service).</summary>
    public IAsyncRelayCommand ApplyAllFansCommand => _applyAllFansCommand ??=
        new AsyncRelayCommand(() => SafeExecuteAsync(ApplyAllFansAsync, "Apply all fans"));

    private IAsyncRelayCommand? _allFansAutoCommand;
    public IAsyncRelayCommand AllFansAutoCommand => _allFansAutoCommand ??=
        new AsyncRelayCommand(() => SafeExecuteAsync(AllFansAutoAsync, "All fans to Auto"));

    private IRelayCommand? _openFansTabCommand;
    public IRelayCommand OpenFansTabCommand => _openFansTabCommand ??= new RelayCommand(() => SelectedTabIndex = 1);

    private IRelayCommand? _unlockBoardFansCommand;
    /// <summary>One click from a locked row: turn "Use BIOS fan settings" off so the header can be set.</summary>
    public IRelayCommand UnlockBoardFansCommand => _unlockBoardFansCommand ??= new RelayCommand(() => UseBiosFanControl = false);

    partial void OnSystemFansPercentChanged(double value)
    {
        if (_suppressProfileSync) return;
        _appSettings.SystemFansPercent = Math.Clamp(value, 20, 100);
    }

    private async Task ApplySystemFansAsync()
    {
        var targets = FanChannels.Where(c => c.Role == CoolingRole.SystemFan && c.CanControl).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = SystemFansHint.Length > 0 ? SystemFansHint : "No controllable system fans.";
            return;
        }
        double duty = Math.Clamp(SystemFansPercent, 20, 100);
        int ok = 0;
        foreach (var c in targets)
        {
            var r = await _hardwareMonitorService.SetFanControlAsync(c.Id, duty, _disposeTokenSource.Token);
            if (r.Success) { ok++; c.SetDesiredFromGroup(duty); c.ApplyResult(r); }
        }
        _ = SaveSettingsAsync();
        StatusMessage = $"System fans: {ok} of {targets.Count} set to {duty:F0}%.";
        await RefreshAsync();
    }

    private async Task ApplyAllFansAsync()
    {
        var targets = FanChannels.Where(c => c.CanControl).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "Nothing to apply — every channel is BIOS-controlled, pump-protected or read-only.";
            return;
        }
        int ok = 0;
        foreach (var c in targets)
        {
            var r = await _hardwareMonitorService.SetFanControlAsync(c.Id, c.DesiredPercent, _disposeTokenSource.Token);
            c.ApplyResult(r);
            if (r.Success)
            {
                ok++;
                if (_activeProfileTab != null) _activeProfileTab.Profile.FanChannelPercents[c.Id] = c.DesiredPercent;
            }
        }
        _lastAiAppliedPercent = null;
        _ = SaveSettingsAsync();
        StatusMessage = $"Applied {ok} of {targets.Count} channel(s).";
        await RefreshAsync();
    }

    private async Task AllFansAutoAsync()
    {
        var targets = FanChannels.Where(c => c.CanControl).ToList();
        foreach (var c in targets)
        {
            var r = await _hardwareMonitorService.RestoreAutomaticControlAsync(c.Id, _disposeTokenSource.Token);
            c.ApplyResult(r);
        }
        _lastAiAppliedPercent = null;
        StatusMessage = targets.Count == 0
            ? "Every channel is already on automatic / BIOS control."
            : $"{targets.Count} channel(s) handed back to automatic / BIOS control.";
        await RefreshAsync();
    }

    // ── Per-poll summary ─────────────────────────────────────────────────────

    /// <summary>True when the dashboard's "CPU cooling" channel is a pump — AI Auto-Fan must leave it alone.</summary>
    private bool _cpuCoolingIsPump;

    private void UpdateCoolingSummary(HardwareSnapshot snapshot)
    {
        var fans = snapshot.Fans;
        _cpuCoolingIsPump = snapshot.CpuFan?.IsPump == true;

        var pumps = fans.Where(f => f.IsPump).ToList();
        HasPump = pumps.Count > 0;
        PumpSummary = pumps.Count == 0
            ? "No pump detected yet — FANZi watches every channel; a pump shows up here within a minute."
            : "Detected: " + string.Join(", ", pumps.Select(p =>
                $"{p.Name} ({(p.SpeedRpm is double r ? $"{r:F0} RPM" : "no RPM")}, found by {EvidenceText(p.Evidence)})")) + ".";

        var system = fans.Where(f => f.Role == CoolingRole.SystemFan).ToList();
        var spinning = system.Where(f => f.SpeedRpm is > 0).Select(f => f.SpeedRpm!.Value).ToList();
        HasSystemFans = system.Count > 0;
        CanControlSystemFans = system.Any(f => f.CanControl);
        SystemFansSummary = system.Count == 0
            ? "No channel is marked as a system fan yet — set a row's role to \"System fan\" to group it here."
            : spinning.Count == 0
                ? $"{system.Count} system fan channel(s), none reporting a speed"
                : spinning.Count == 1
                    ? $"{system.Count} system fan channel(s) · {spinning[0]:F0} RPM"
                    : $"{system.Count} system fan channel(s) · {spinning.Min():F0}–{spinning.Max():F0} RPM";
        SystemFansHint = !HasSystemFans ? ""
            : CanControlSystemFans ? ""
            : system.All(f => f.LockReason == FanLockReason.BiosControl)
                ? "Locked: \"Use BIOS fan settings\" is on. Turn it off to set system fans from FANZi."
                : "These headers do not accept software control on this board.";
        SystemFansHud = spinning.Count > 0 ? $"{spinning.Average():F0} RPM" : "—";

        int bios = fans.Count(f => f.LockReason == FanLockReason.BiosControl);
        int pumpLocked = fans.Count(f => f.LockReason == FanLockReason.PumpProtected);
        int readOnly = fans.Count(f => f.LockReason == FanLockReason.ReadOnlyHardware);
        var parts = new List<string>();
        if (bios > 0) parts.Add($"{bios} on BIOS control");
        if (pumpLocked > 0) parts.Add($"{pumpLocked} pump-protected");
        if (readOnly > 0) parts.Add($"{readOnly} read-only");
        FanLockSummary = parts.Count == 0 ? "" : "Apply is off for: " + string.Join(" · ", parts) + ".";
        HasFanLockSummary = parts.Count > 0;
        HasPendingFanChanges = FanChannels.Any(c => c.CanControl && c.HasPendingChange);

        var chips = fans
            .OrderBy(f => f.Role switch { CoolingRole.CpuFan => 0, CoolingRole.Pump => 1, CoolingRole.SystemFan => 2, CoolingRole.GpuFan => 4, _ => 3 })
            .ThenBy(f => f.SensorIndex)
            .Select(f => new CoolingChip(
                ChipLabel(f),
                f.SpeedRpm is double r ? $"{r:F0} RPM" : f.CurrentControlPercent is double d ? $"{d:F0}%" : "—",
                f.IsPump ? "#A78BFA" : f.Role switch { CoolingRole.CpuFan => "#38BDF8", CoolingRole.GpuFan => "#34D399", CoolingRole.SystemFan => "#FBBF24", _ => "#8FB3DA" },
                $"{f.Name} · {f.HardwareSource}" + (f.CurrentControlPercent is double duty ? $" · duty {duty:F0}%" : "")))
            .ToList();
        SyncInPlace(CoolingChips, chips);
    }

    private static string ChipLabel(FanChannelSnapshot f) => f.IsPump ? "PUMP"
        : f.Role switch
        {
            CoolingRole.CpuFan => "CPU",
            CoolingRole.GpuFan => "GPU " + (f.SensorIndex >= 0 ? (f.SensorIndex + 1).ToString() : ""),
            CoolingRole.SystemFan => "SYS " + (f.SensorIndex >= 0 ? (f.SensorIndex + 1).ToString() : ""),
            _ => f.Name.Length <= 10 ? f.Name.ToUpperInvariant() : "FAN " + (f.SensorIndex >= 0 ? (f.SensorIndex + 1).ToString() : ""),
        };

    internal static string EvidenceText(CoolingEvidence e) => e switch
    {
        CoolingEvidence.Name => "its name",
        CoolingEvidence.Hardware => "the cooler it belongs to",
        CoolingEvidence.RpmSignature => "its steady high speed",
        CoolingEvidence.UserOverride => "your choice",
        _ => "default",
    };
}
