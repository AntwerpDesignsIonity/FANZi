using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly IHardwareMonitorService _hardwareMonitorService;
    private readonly CancellationTokenSource _disposeTokenSource = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cpuFanChannelId;
    private bool _suppressCpuFanDesiredUpdate;
    private bool _disposed;

    [ObservableProperty]
    private string _statusMessage = "Initializing hardware monitor...";

    [ObservableProperty]
    private string _cpuPackageTemperature = "--";

    [ObservableProperty]
    private string _cpuAverageTemperature = "--";

    [ObservableProperty]
    private string _cpuHotspotTemperature = "--";

    [ObservableProperty]
    private string _cpuTotalLoad = "--";

    [ObservableProperty]
    private string _cpuAverageClock = "--";

    [ObservableProperty]
    private string _cpuPackagePower = "--";

    [ObservableProperty]
    private string _cpuCoreVoltage = "--";

    [ObservableProperty]
    private string _gpuCoreTemperature = "--";

    [ObservableProperty]
    private string _gpuHotspotTemperature = "--";

    [ObservableProperty]
    private string _gpuLoad = "--";

    [ObservableProperty]
    private string _gpuClock = "--";

    [ObservableProperty]
    private string _gpuPower = "--";

    [ObservableProperty]
    private string _cpuName = string.Empty;

    [ObservableProperty]
    private string _gpuName = string.Empty;

    [ObservableProperty]
    private string _gpuVram = string.Empty;

    [ObservableProperty]
    private string _cpuFanName = "CPU fan";

    [ObservableProperty]
    private string _cpuFanSpeed = "Not detected";

    [ObservableProperty]
    private string _cpuFanControl = "--";

    [ObservableProperty]
    private string _cpuFanCapability = "Waiting for hardware sample...";

    [ObservableProperty]
    private bool _cpuFanCanControl;

    [ObservableProperty]
    private double _cpuFanDesiredPercent;

    [ObservableProperty]
    private string _cpuFanDesiredLabel = "--";

    [ObservableProperty]
    private bool _isCpuFanBusy;

    [ObservableProperty]
    private string _fanCountLabel = "0 channels";

    [ObservableProperty]
    private string _lastUpdated = "Waiting for first sample";

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _showHelp;

    [ObservableProperty]
    private double _cpuWarningThresholdDegrees = 95;

    [ObservableProperty]
    private bool _hasCpuTempWarning;

    [ObservableProperty]
    private string _cpuTempWarningMessage = string.Empty;

    [ObservableProperty]
    private string _notificationEmail = string.Empty;

    [ObservableProperty]
    private bool _hasNotificationEmail;

    public MainWindowViewModel(IHardwareMonitorService hardwareMonitorService)
    {
        _hardwareMonitorService = hardwareMonitorService;
        FanChannels = new ObservableCollection<FanChannelViewModel>();
        CpuReadings = new ObservableCollection<CpuReadingSnapshot>();
        GpuReadings = new ObservableCollection<GpuReadingSnapshot>();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApplyCpuFanCommand = new AsyncRelayCommand(ApplyCpuFanAsync);
        AutoCpuFanCommand = new AsyncRelayCommand(AutoCpuFanAsync);
        ToggleHelpCommand = new RelayCommand(() => ShowHelp = !ShowHelp);

        _ = RunStartupAsync();
    }

    public ObservableCollection<FanChannelViewModel> FanChannels { get; }

    public ObservableCollection<CpuReadingSnapshot> CpuReadings { get; }

    public ObservableCollection<GpuReadingSnapshot> GpuReadings { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand ApplyCpuFanCommand { get; }

    public IAsyncRelayCommand AutoCpuFanCommand { get; }

    public IRelayCommand ToggleHelpCommand { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeTokenSource.Cancel();
        _refreshLock.Dispose();
        _disposeTokenSource.Dispose();

        foreach (FanChannelViewModel fanChannel in FanChannels)
        {
            fanChannel.Dispose();
        }
    }

    private async Task RunStartupAsync()
    {
        // 2-second splash / loader animation.
        await Task.Delay(TimeSpan.FromSeconds(2));
        IsLoading = false;
        await RunRefreshLoopAsync();
    }

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            while (!_disposeTokenSource.IsCancellationRequested)
            {
                await RefreshAsync();
                await Task.Delay(RefreshInterval, _disposeTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsRefreshing = true;

            HardwareSnapshot snapshot = await _hardwareMonitorService.GetSnapshotAsync(_disposeTokenSource.Token);

            CpuPackageTemperature = FormatTemperature(snapshot.CpuPackageTemperature);
            CpuAverageTemperature = FormatTemperature(snapshot.CpuAverageTemperature);
            CpuHotspotTemperature = FormatTemperature(snapshot.CpuHotspotTemperature);
            CpuTotalLoad = FormatPercent(snapshot.CpuTotalLoadPercent);
            CpuAverageClock = FormatClock(snapshot.CpuAverageClockMhz);
            CpuPackagePower = FormatPower(snapshot.CpuPackagePowerWatts);
            CpuCoreVoltage = FormatVoltage(snapshot.CpuCoreVoltage);
            GpuCoreTemperature = FormatTemperature(snapshot.GpuCoreTemperature);
            GpuHotspotTemperature = FormatTemperature(snapshot.GpuHotspotTemperature);
            GpuLoad = FormatPercent(snapshot.GpuLoadPercent);
            GpuClock = FormatClock(snapshot.GpuCoreClockMhz);
            GpuPower = FormatPower(snapshot.GpuPowerWatts);
            CpuName = snapshot.CpuName ?? string.Empty;
            GpuName = snapshot.GpuName ?? string.Empty;
            GpuVram = FormatVram(snapshot.GpuMemoryUsedMb, snapshot.GpuMemoryTotalMb);
            CpuFanName = snapshot.CpuFan?.Name ?? "CPU fan";
            CpuFanSpeed = snapshot.CpuFan?.SpeedRpm is double rpm ? $"{rpm:F0} RPM" : "Not detected";
            CpuFanControl = snapshot.CpuFan?.CurrentControlPercent is double control ? $"{control:F0}%" : "Auto/BIOS";
            CpuFanCapability = snapshot.CpuFan?.CapabilityMessage ?? "No dedicated CPU fan header was detected in the current sensor set.";
            CpuFanCanControl = snapshot.CpuFan?.CanControl == true;
            _cpuFanChannelId = snapshot.CpuFan?.Id;

            if (snapshot.CpuFan?.CurrentControlPercent is double cpuFanPercent)
            {
                SetCpuFanDesiredPercent(cpuFanPercent);
            }

            FanCountLabel = snapshot.Fans.Count == 1 ? "1 channel" : $"{snapshot.Fans.Count} channels";
            LastUpdated = $"Updated {snapshot.Timestamp.LocalDateTime:HH:mm:ss}";
            StatusMessage = snapshot.StatusMessage;

            // ── Temperature warning check ──────────────────────────
            double? hottest = snapshot.CpuHotspotTemperature ?? snapshot.CpuPackageTemperature ?? snapshot.CpuAverageTemperature;
            if (hottest.HasValue && hottest.Value >= CpuWarningThresholdDegrees)
            {
                HasCpuTempWarning = true;
                string emailNote = HasNotificationEmail ? $" Notification queued to {NotificationEmail}." : " Set a notification e-mail below to receive reports.";
                CpuTempWarningMessage = $"⚠  CPU temperature {hottest.Value:F0} °C exceeds the {CpuWarningThresholdDegrees:F0} °C threshold.{emailNote}";
            }
            else
            {
                HasCpuTempWarning = false;
                CpuTempWarningMessage = string.Empty;
            }

            SyncCpuReadings(snapshot.CpuReadings);
            SyncGpuReadings(snapshot.GpuReadings);
            SynchronizeFans(snapshot.Fans);
        }
        catch (OperationCanceledException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            // Do not surface internal path/handle details from the exception message.
            StatusMessage = "Hardware poll failed: access was denied to a sensor. Ensure the application is running as Administrator.";
        }
        catch (Exception)
        {
            // Swallow the raw exception.Message to prevent leaking internal paths or
            // system details to the UI. The previous reading remains visible.
            StatusMessage = "Hardware poll encountered an error. The previous reading is still displayed.";
        }
        finally
        {
            IsRefreshing = false;
            _refreshLock.Release();
        }
    }

    private void SynchronizeFans(IReadOnlyList<FanChannelSnapshot> fans)
    {
        Dictionary<string, FanChannelViewModel> existing = FanChannels.ToDictionary(channel => channel.Id, StringComparer.OrdinalIgnoreCase);

        foreach (FanChannelSnapshot snapshot in fans)
        {
            if (!existing.TryGetValue(snapshot.Id, out FanChannelViewModel? channelViewModel))
            {
                channelViewModel = new FanChannelViewModel(snapshot, ApplyFanControlAsync, RestoreAutomaticControlAsync);
                FanChannels.Add(channelViewModel);
                continue;
            }

            channelViewModel.Update(snapshot);
            existing.Remove(snapshot.Id);
        }

        foreach ((_, FanChannelViewModel channelViewModel) in existing)
        {
            channelViewModel.Dispose();
            FanChannels.Remove(channelViewModel);
        }
    }

    private void SyncCpuReadings(IReadOnlyList<CpuReadingSnapshot> readings)
    {
        CpuReadings.Clear();
        foreach (CpuReadingSnapshot reading in readings)
        {
            CpuReadings.Add(reading);
        }
    }

    private void SyncGpuReadings(IReadOnlyList<GpuReadingSnapshot> readings)
    {
        GpuReadings.Clear();
        foreach (GpuReadingSnapshot reading in readings)
        {
            GpuReadings.Add(reading);
        }
    }

    private async Task ApplyFanControlAsync(FanChannelViewModel channel)
    {
        SetFanControlResult result = await _hardwareMonitorService.SetFanControlAsync(channel.Id, channel.DesiredPercent, _disposeTokenSource.Token);
        channel.ApplyResult(result);
        await RefreshAsync();
    }

    private async Task RestoreAutomaticControlAsync(FanChannelViewModel channel)
    {
        SetFanControlResult result = await _hardwareMonitorService.RestoreAutomaticControlAsync(channel.Id, _disposeTokenSource.Token);
        channel.ApplyResult(result);
        await RefreshAsync();
    }

    partial void OnCpuFanDesiredPercentChanged(double value)
    {
        CpuFanDesiredLabel = $"{value:F0}%";
    }

    partial void OnCpuWarningThresholdDegreesChanged(double value)
    {
        // Enforce the valid range in code, not just via the AXAML NumericUpDown.
        double clamped = Math.Clamp(value, 50, 110);
        if (clamped != value)
        {
            CpuWarningThresholdDegrees = clamped;
        }
    }

    partial void OnNotificationEmailChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            HasNotificationEmail = false;
            return;
        }

        // Validate using the BCL parser so arbitrary strings are rejected before
        // they can reach any future email-sending code path.
        try
        {
            _ = new MailAddress(value.Trim());
            HasNotificationEmail = true;
        }
        catch (FormatException)
        {
            HasNotificationEmail = false;
        }
    }

    private async Task ApplyCpuFanAsync()
    {
        if (!CpuFanCanControl || string.IsNullOrWhiteSpace(_cpuFanChannelId))
        {
            StatusMessage = "CPU fan control is not available on this hardware.";
            return;
        }

        IsCpuFanBusy = true;
        try
        {
            SetFanControlResult result = await _hardwareMonitorService.SetFanControlAsync(_cpuFanChannelId, CpuFanDesiredPercent, _disposeTokenSource.Token);
            CpuFanCapability = result.Message;
            await RefreshAsync();
        }
        finally
        {
            IsCpuFanBusy = false;
        }
    }

    private async Task AutoCpuFanAsync()
    {
        if (!CpuFanCanControl || string.IsNullOrWhiteSpace(_cpuFanChannelId))
        {
            StatusMessage = "CPU fan auto mode is not available on this hardware.";
            return;
        }

        IsCpuFanBusy = true;
        try
        {
            SetFanControlResult result = await _hardwareMonitorService.RestoreAutomaticControlAsync(_cpuFanChannelId, _disposeTokenSource.Token);
            CpuFanCapability = result.Message;
            await RefreshAsync();
        }
        finally
        {
            IsCpuFanBusy = false;
        }
    }

    private void SetCpuFanDesiredPercent(double value)
    {
        if (_suppressCpuFanDesiredUpdate)
        {
            return;
        }

        _suppressCpuFanDesiredUpdate = true;
        CpuFanDesiredPercent = Math.Clamp(value, 0, 100);
        _suppressCpuFanDesiredUpdate = false;
    }

    private static string FormatTemperature(double? temperature)
    {
        return temperature.HasValue ? $"{temperature.Value:F1} C" : "--";
    }

    private static string FormatPercent(double? value)
    {
        return value.HasValue ? $"{value.Value:F0}%" : "--";
    }

    private static string FormatClock(double? value)
    {
        return value.HasValue ? $"{value.Value:F0} MHz" : "--";
    }

    private static string FormatPower(double? value)
    {
        return value.HasValue ? $"{value.Value:F1} W" : "--";
    }

    private static string FormatVoltage(double? value)
    {
        return value.HasValue ? $"{value.Value:F3} V" : "--";
    }

    private static string FormatVram(double? usedMb, double? totalMb)
    {
        if (!usedMb.HasValue && !totalMb.HasValue)
        {
            return string.Empty;
        }

        if (usedMb.HasValue && totalMb.HasValue)
        {
            double usedGb = usedMb.Value / 1024.0;
            double totalGb = totalMb.Value / 1024.0;
            return $"{usedGb:F1} / {totalGb:F1} GB";
        }

        if (totalMb.HasValue)
        {
            return $"{totalMb.Value / 1024.0:F1} GB";
        }

        return $"{usedMb!.Value / 1024.0:F1} GB used";
    }
}
