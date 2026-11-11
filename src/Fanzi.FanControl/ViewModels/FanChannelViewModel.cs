using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Fanzi.FanControl.ViewModels;

public partial class FanChannelViewModel : ViewModelBase, IDisposable
{
    private readonly Func<FanChannelViewModel, Task> _applyAction;
    private readonly Func<FanChannelViewModel, Task> _autoAction;
    private bool _disposed;
    private bool _suppressDirtyTracking;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _speedLabel;

    [ObservableProperty]
    private string _currentControlLabel;

    [ObservableProperty]
    private string _desiredControlLabel;

    [ObservableProperty]
    private string _capabilityMessage;

    [ObservableProperty]
    private bool _canControl;

    [ObservableProperty]
    private bool _hasPendingChange;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _desiredPercent;

    [ObservableProperty]
    private string _deviceKindBadge;

    [ObservableProperty]
    private string _sourceLabel;

    [ObservableProperty]
    private bool _showMinSpeedWarning;

    // ── 4.5: role, lock reason, pump ─────────────────────────────────────────

    /// <summary>Role choices for the row's picker.</summary>
    public static IReadOnlyList<string> RoleChoices { get; } = ["Auto-detect", "CPU fan", "System fan", "Pump", "GPU fan"];
    public IReadOnlyList<string> Roles => RoleChoices;

    private static readonly CoolingRole[] RoleOrder = [CoolingRole.Auto, CoolingRole.CpuFan, CoolingRole.SystemFan, CoolingRole.Pump, CoolingRole.GpuFan];

    private Action<FanChannelViewModel, CoolingRole>? _roleChosen;
    private bool _suppressRole;

    /// <summary>The role FANZi uses for this channel right now (detected or chosen).</summary>
    public CoolingRole Role { get; private set; }

    /// <summary>Picker selection: the user's saved choice ("Auto-detect" when none).</summary>
    [ObservableProperty] private int _roleChoiceIndex;

    [ObservableProperty] private bool _isPump;
    [ObservableProperty] private string _roleLabel = "Fan";
    [ObservableProperty] private string _lockText = string.Empty;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private bool _isBiosLocked;
    [ObservableProperty] private string _badgeColor = "#3B82F6";
    [ObservableProperty] private double _sliderMinimum;

    /// <summary>Wires the role picker (set by the owner once; the row itself stays persistence-free).</summary>
    public void SetRoleHandler(Action<FanChannelViewModel, CoolingRole> handler, CoolingRole savedChoice)
    {
        _roleChosen = handler;
        _suppressRole = true;
        RoleChoiceIndex = Math.Max(0, Array.IndexOf(RoleOrder, savedChoice));
        _suppressRole = false;
    }

    partial void OnRoleChoiceIndexChanged(int value)
    {
        if (_suppressRole || value < 0 || value >= RoleOrder.Length) return;
        _roleChosen?.Invoke(this, RoleOrder[value]);
    }

    /// <summary>The group slider set this row's target (no "pending" flag — it was applied).</summary>
    public void SetDesiredFromGroup(double percent) => SetDesiredPercent(percent);

    public FanChannelViewModel(
        FanChannelSnapshot snapshot,
        Func<FanChannelViewModel, Task> applyAction,
        Func<FanChannelViewModel, Task> autoAction)
    {
        Id = snapshot.Id;
        _applyAction = applyAction;
        _autoAction = autoAction;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync);
        AutoCommand = new AsyncRelayCommand(RestoreAutomaticAsync);

        _name = string.Empty;
        _speedLabel = string.Empty;
        _currentControlLabel = string.Empty;
        _desiredControlLabel = string.Empty;
        _capabilityMessage = string.Empty;
        _deviceKindBadge = string.Empty;
        _sourceLabel = string.Empty;

        Update(snapshot);
    }

    public string Id { get; }

    public IAsyncRelayCommand ApplyCommand { get; }

    public IAsyncRelayCommand AutoCommand { get; }

    public void Dispose()
    {
        _disposed = true;
    }

    public void Update(FanChannelSnapshot snapshot)
    {
        Name = snapshot.Name;
        SpeedLabel = snapshot.SpeedRpm.HasValue ? $"{snapshot.SpeedRpm.Value:F0} RPM" : "No RPM reading";
        CurrentControlLabel = snapshot.CurrentControlPercent.HasValue ? $"{snapshot.CurrentControlPercent.Value:F0}%" : "Auto/BIOS";
        CapabilityMessage = snapshot.CapabilityMessage;
        CanControl = snapshot.CanControl;
        Role = snapshot.Role;
        IsPump = snapshot.IsPump;
        DeviceKindBadge = snapshot.IsPump
            ? (snapshot.Evidence == CoolingEvidence.RpmSignature ? "PUMP · detected" : "PUMP")
            : snapshot.Role switch
            {
                CoolingRole.CpuFan => "CPU FAN",
                CoolingRole.SystemFan => "SYSTEM FAN",
                CoolingRole.GpuFan => "GPU FAN",
                _ => "FAN",
            };
        BadgeColor = snapshot.IsPump ? "#A78BFA" : snapshot.Role switch
        {
            CoolingRole.CpuFan => "#38BDF8",
            CoolingRole.SystemFan => "#FBBF24",
            CoolingRole.GpuFan => "#34D399",
            _ => "#3B82F6",
        };
        RoleLabel = snapshot.IsPump ? "Pump" : CoolingChannelClassifier.Label(snapshot.Role == CoolingRole.Auto ? CoolingRole.Auto : snapshot.Role);
        IsLocked = snapshot.LockReason != FanLockReason.None;
        IsBiosLocked = snapshot.LockReason == FanLockReason.BiosControl;
        LockText = snapshot.LockReason switch
        {
            FanLockReason.BiosControl => "Locked — BIOS fan settings own this header",
            FanLockReason.PumpProtected => "Locked — pump protection",
            FanLockReason.ReadOnlyHardware => "Read-only — this hardware has no software control",
            _ => string.Empty,
        };
        // An unlocked pump can only be set inside its safe window.
        SliderMinimum = snapshot.IsPump && snapshot.CanControl ? PumpProtection.MinPercent : 0;

        // Build source label: e.g. "#2 · ASUS ROG Strix B550-F"
        string indexPart = snapshot.SensorIndex >= 0 ? $"#{snapshot.SensorIndex + 1}" : string.Empty;
        string sourcePart = string.IsNullOrWhiteSpace(snapshot.HardwareSource) ? string.Empty : snapshot.HardwareSource;
        SourceLabel = (indexPart, sourcePart) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{indexPart} · {sourcePart}",
            ({ Length: > 0 }, _) => indexPart,
            (_, { Length: > 0 }) => sourcePart,
            _ => string.Empty
        };

        if (!HasPendingChange)
        {
            SetDesiredPercent(snapshot.CurrentControlPercent ?? DesiredPercent);
        }

        DesiredControlLabel = $"{DesiredPercent:F0}%";
    }

    public void ApplyResult(SetFanControlResult result)
    {
        CapabilityMessage = result.Message;
        HasPendingChange = false;
    }

    partial void OnDesiredPercentChanged(double value)
    {
        DesiredControlLabel = $"{value:F0}%";
        ShowMinSpeedWarning = CanControl && !IsPump && value > 0 && value < 20;

        if (!_suppressDirtyTracking)
        {
            HasPendingChange = true;
        }
    }

    private async Task ApplyAsync()
    {
        if (_disposed || !CanControl)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _applyAction(this);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAutomaticAsync()
    {
        if (_disposed || !CanControl)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _autoAction(this);
            HasPendingChange = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetDesiredPercent(double value)
    {
        _suppressDirtyTracking = true;
        DesiredPercent = Math.Clamp(value, 0, 100);
        _suppressDirtyTracking = false;
    }
}