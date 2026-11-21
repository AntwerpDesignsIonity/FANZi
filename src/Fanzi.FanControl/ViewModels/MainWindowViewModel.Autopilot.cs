using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// 4.6 AEDi Autopilot: one switch and one choice (Quiet / Balanced / Cool) on the Dashboard. While it is on,
/// <see cref="AutopilotBrain"/> picks the fan curve for what the PC is doing and FANZi drives the CPU fan and the
/// system fans from it. Safety rules are the same as everywhere else in FANZi and are enforced twice — here and in
/// the hardware service: a pump is never driven, and a header the BIOS owns ("Use BIOS fan settings") or that is
/// read-only is only watched. Switching Autopilot off hands every channel it touched back to automatic control.
/// </summary>
public partial class MainWindowViewModel
{
    private readonly AutopilotBrain _autopilot = new();

    /// <summary>Last duty Autopilot wrote per system-fan channel (id → %); also the list handed back on switch-off.</summary>
    private readonly Dictionary<string, double> _autopilotApplied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>System-fan writes happen only when the target moved by at least this much (%).</summary>
    internal const double AutopilotWriteStepPercent = 2.0;

    private bool _suppressAutopilotSync;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FanCurveStatus), nameof(AutopilotSwitchLabel))]
    private bool _autopilotEnabled;

    /// <summary>0 = Quiet, 1 = Balanced, 2 = Cool.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutopilotQuiet), nameof(IsAutopilotBalanced), nameof(IsAutopilotCool))]
    private int _autopilotGoalIndex = 1;

    [ObservableProperty] private bool _autopilotQuietHours = true;

    [ObservableProperty] private string _autopilotHeadline = "Off";
    [ObservableProperty] private string _autopilotDetail = AutopilotOffDetail;

    /// <summary>Shown when Autopilot is on but may not set some / any fans (BIOS-owned headers).</summary>
    [ObservableProperty] private string _autopilotHint = "";
    [ObservableProperty] private bool _hasAutopilotHint;

    /// <summary>True while Autopilot is on and the board fans are still locked to the BIOS — offers the unlock button.</summary>
    [ObservableProperty] private bool _autopilotCanUnlockBoardFans;

    internal const string AutopilotOffDetail =
        "One switch for cooling: AEDi watches what the PC is doing and sets the fans for it. Pumps are never touched.";

    public string AutopilotSwitchLabel => AutopilotEnabled ? "On" : "Off";

    public bool IsAutopilotQuiet { get => AutopilotGoalIndex == 0; set { if (value) AutopilotGoalIndex = 0; } }
    public bool IsAutopilotBalanced { get => AutopilotGoalIndex == 1; set { if (value) AutopilotGoalIndex = 1; } }
    public bool IsAutopilotCool { get => AutopilotGoalIndex == 2; set { if (value) AutopilotGoalIndex = 2; } }

    internal AutopilotBrain Autopilot => _autopilot;

    private IRelayCommand? _autopilotUnlockBoardFansCommand;

    /// <summary>"Let Autopilot set the board fans": turns "Use BIOS fan settings" off (the user's explicit choice).</summary>
    public IRelayCommand AutopilotUnlockBoardFansCommand => _autopilotUnlockBoardFansCommand ??= new RelayCommand(() =>
    {
        UseBiosFanControl = false;
        AppLog.Info("Autopilot: user allowed FANZi to set motherboard fan headers.");
    });

    internal static AutopilotGoal GoalFromIndex(int index) => index switch
    {
        0 => AutopilotGoal.Quiet,
        2 => AutopilotGoal.Cool,
        _ => AutopilotGoal.Balanced,
    };

    internal static int IndexFromGoal(string? goal) => goal switch
    {
        "Quiet" => 0,
        "Cool" => 2,
        _ => 1,
    };

    /// <summary>Called from <see cref="ApplySettingsToVm"/>.</summary>
    private void ApplyAutopilotSettings()
    {
        _suppressAutopilotSync = true;
        try
        {
            AutopilotGoalIndex = IndexFromGoal(_appSettings.AutopilotGoal);
            AutopilotQuietHours = _appSettings.AutopilotQuietHours;
            AutopilotEnabled = _appSettings.AutopilotEnabled;
        }
        finally
        {
            _suppressAutopilotSync = false;
        }
        _autopilot.Goal = GoalFromIndex(AutopilotGoalIndex);
        _autopilot.QuietHoursEnabled = AutopilotQuietHours;
    }

    partial void OnAutopilotGoalIndexChanged(int value)
    {
        _autopilot.Goal = GoalFromIndex(value);
        if (_suppressAutopilotSync) return;
        _appSettings.AutopilotGoal = _autopilot.Goal.ToString();
        _ = SaveSettingsAsync();
    }

    partial void OnAutopilotQuietHoursChanged(bool value)
    {
        _autopilot.QuietHoursEnabled = value;
        if (_suppressAutopilotSync) return;
        _appSettings.AutopilotQuietHours = value;
        _ = SaveSettingsAsync();
    }

    partial void OnAutopilotEnabledChanged(bool value)
    {
        if (value)
        {
            _autopilot.Reset();
            AutopilotHeadline = "Starting…";
            AutopilotDetail = "Reading the first samples.";
        }
        else
        {
            AutopilotHeadline = "Off";
            AutopilotDetail = AutopilotOffDetail;
            AutopilotHint = "";
            HasAutopilotHint = false;
            AutopilotCanUnlockBoardFans = false;
        }

        if (_suppressAutopilotSync) return;

        _appSettings.AutopilotEnabled = value;
        _ = SaveSettingsAsync();
        AppLog.Info(value ? $"Autopilot on (goal {_autopilot.Goal})." : "Autopilot off — handing fans back to automatic control.");
        StatusMessage = value
            ? "Autopilot on — AEDi sets the fans for what the PC is doing."
            : "Autopilot off — fans are back on automatic / BIOS control.";

        if (!value)
            _ = SafeExecuteAsync(ReleaseAutopilotChannelsAsync, "Autopilot hand-back");
    }

    /// <summary>
    /// One Autopilot step (UI thread, inside the hardware poll). Returns the decision whose curve the CPU-fan engine
    /// follows this poll, or null when Autopilot is off.
    /// </summary>
    private AutopilotDecision? StepAutopilot(HardwareSnapshot snapshot, double? hottestCpuTempC)
    {
        if (!AutopilotEnabled) return null;

        var decision = _autopilot.Decide(new AutopilotInputs(
            hottestCpuTempC,
            snapshot.GpuCoreTemperature,
            snapshot.CpuTotalLoadPercent ?? 0,
            snapshot.GpuLoadPercent ?? 0,
            IsGameMode,
            DetectedGame,
            DateTime.Now));

        AutopilotHeadline = decision.Headline;
        return decision;
    }

    /// <summary>
    /// Sends the decision's duty to every system fan (and any extra CPU-fan header) FANZi may set, then says in plain
    /// words what Autopilot is — and is not — driving.
    /// </summary>
    private async Task ApplyAutopilotAsync(AutopilotDecision decision, double? cpuFanPercent)
    {
        var followers = FanChannels
            .Where(c => !c.IsPump && c.CanControl
                        && (c.Role == CoolingRole.SystemFan || c.Role == CoolingRole.CpuFan)
                        && !string.Equals(c.Id, _cpuFanChannelId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var channel in followers)
        {
            // An extra CPU header (CPU_OPT, second tower fan) follows the CPU fan; case fans follow the system duty.
            double target = channel.Role == CoolingRole.CpuFan && cpuFanPercent is double cpu ? cpu : decision.SystemFanPercent;
            target = Math.Clamp(Math.Round(target), AutopilotBrain.MinSystemFanPercent, 100);

            if (_autopilotApplied.TryGetValue(channel.Id, out double last) && Math.Abs(target - last) < AutopilotWriteStepPercent)
                continue;

            var result = await _hardwareMonitorService.SetFanControlAsync(channel.Id, target, _disposeTokenSource.Token);
            if (result.Success)
            {
                _autopilotApplied[channel.Id] = target;
                channel.SetDesiredFromGroup(target);
            }
        }

        // Channels that disappeared or were locked since (BIOS switch, pump role): forget them.
        if (_autopilotApplied.Count > 0)
        {
            var live = new HashSet<string>(followers.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var id in _autopilotApplied.Keys.Where(id => !live.Contains(id)).ToList())
                _autopilotApplied.Remove(id);
        }

        bool drivesCpu = CpuFanCanControl && !_cpuCoolingIsPump && !string.IsNullOrEmpty(_cpuFanChannelId);
        int driven = followers.Count + (drivesCpu ? 1 : 0);
        int biosLocked = FanChannels.Count(c => c.IsBiosLocked);

        string doing = driven switch
        {
            0 => "Watching only",
            1 => "Driving 1 fan",
            _ => $"Driving {driven} fans",
        };
        string cpuText = drivesCpu && cpuFanPercent is double cp ? $" · CPU fan {cp:F0}%" : "";
        string sysText = followers.Count > 0 ? $" · system {decision.SystemFanPercent:F0}%" : "";
        AutopilotDetail = $"{doing}{cpuText}{sysText}. {decision.Reason}";

        if (biosLocked > 0)
        {
            AutopilotHint = driven == 0
                ? $"{biosLocked} motherboard fan header(s) follow your BIOS settings, so Autopilot is only watching them."
                : $"{biosLocked} motherboard fan header(s) still follow your BIOS settings.";
            HasAutopilotHint = true;
            AutopilotCanUnlockBoardFans = true;
        }
        else if (driven == 0)
        {
            AutopilotHint = FanChannels.Count == 0
                ? "No fan channels are reported by this PC — Autopilot can only watch temperatures."
                : "This PC's fans are read-only or pump-protected — Autopilot is watching temperatures.";
            HasAutopilotHint = true;
            AutopilotCanUnlockBoardFans = false;
        }
        else
        {
            AutopilotHint = "";
            HasAutopilotHint = false;
            AutopilotCanUnlockBoardFans = false;
        }
    }

    /// <summary>Autopilot off: every channel it set goes back to automatic / BIOS control.</summary>
    private async Task ReleaseAutopilotChannelsAsync()
    {
        var ids = _autopilotApplied.Keys.ToList();
        _autopilotApplied.Clear();

        // The CPU fan stays with AI Auto-Fan when that switch is on by itself.
        if (!AiAutoFanEnabled && CpuFanCanControl && _cpuFanChannelId is { Length: > 0 } cpuId && _lastAiAppliedPercent is not null)
            ids.Add(cpuId);
        if (!AiAutoFanEnabled)
        {
            _lastAiAppliedPercent = null;
            _lastAiAppliedChannelId = null;
            _aiEngine.Reset();
        }

        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            await _hardwareMonitorService.RestoreAutomaticControlAsync(id, _disposeTokenSource.Token);

        if (ids.Count > 0) await RefreshAsync();
    }
}
