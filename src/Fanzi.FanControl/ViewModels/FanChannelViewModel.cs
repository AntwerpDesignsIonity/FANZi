using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using System;
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
        DeviceKindBadge = snapshot.DeviceKind switch
        {
            FanDeviceKind.Pump => "⭐ PUMP",
            FanDeviceKind.AioCooler => "❄ AIO",
            _ => "⚡ FAN"
        };

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
        ShowMinSpeedWarning = CanControl && value > 0 && value < 20;

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