using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly IHardwareMonitorService _hardwareMonitorService;
    private readonly ISettingsService _settingsService;
    private readonly CancellationTokenSource _disposeTokenSource = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AppSettings _appSettings = new();
    private ProfileTabViewModel? _activeProfileTab;
    private string? _cpuFanChannelId;
    private bool _suppressCpuFanDesiredUpdate;
    private bool _suppressProfileSync;
    private bool _disposed;
    private readonly EmailNotificationService _emailService = new();
    private DateTimeOffset _lastAlertEmailSent = DateTimeOffset.MinValue;
    private static readonly TimeSpan AlertEmailCooldown = TimeSpan.FromMinutes(10);

    public RgbControlViewModel RgbControl { get; }

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

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _smtpHost = string.Empty;

    [ObservableProperty]
    private double _smtpPort = 587;

    [ObservableProperty]
    private string _smtpUser = string.Empty;

    [ObservableProperty]
    private string _smtpPassword = string.Empty;

    [ObservableProperty]
    private bool _smtpConfigured;

    [ObservableProperty]
    private string _emailStatus = string.Empty;

    [ObservableProperty]
    private bool _isSendingEmail;

    public MainWindowViewModel(IHardwareMonitorService hardwareMonitorService, IRgbService rgbService, ISettingsService settingsService)
    {
        _hardwareMonitorService = hardwareMonitorService;
        _settingsService        = settingsService;
        FanChannels             = new ObservableCollection<FanChannelViewModel>();
        CpuReadings             = new ObservableCollection<CpuReadingSnapshot>();
        GpuReadings             = new ObservableCollection<GpuReadingSnapshot>();
        Profiles                = new ObservableCollection<ProfileTabViewModel>();
        RefreshCommand          = new AsyncRelayCommand(RefreshAsync);
        ApplyCpuFanCommand      = new AsyncRelayCommand(ApplyCpuFanAsync);
        AutoCpuFanCommand       = new AsyncRelayCommand(AutoCpuFanAsync);
        ToggleHelpCommand       = new RelayCommand(() => ShowHelp = !ShowHelp);
        AddProfileCommand       = new RelayCommand(AddProfile);
        SendTestEmailCommand    = new AsyncRelayCommand(SendTestEmailAsync);
        RgbControl              = new RgbControlViewModel(rgbService);

        _ = RunStartupAsync();
    }

    public ObservableCollection<FanChannelViewModel> FanChannels { get; }

    public ObservableCollection<CpuReadingSnapshot> CpuReadings { get; }

    public ObservableCollection<GpuReadingSnapshot> GpuReadings { get; }

    public ObservableCollection<ProfileTabViewModel> Profiles { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand ApplyCpuFanCommand { get; }

    public IAsyncRelayCommand AutoCpuFanCommand { get; }

    public IRelayCommand ToggleHelpCommand { get; }

    public IRelayCommand AddProfileCommand { get; }

    public IAsyncRelayCommand SendTestEmailCommand { get; }

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
        RgbControl.Dispose();

        foreach (FanChannelViewModel fanChannel in FanChannels)
        {
            fanChannel.Dispose();
        }
    }

    private async Task RunStartupAsync()
    {
        // Load persisted settings concurrently with the splash delay.
        Task<AppSettings> loadTask = _settingsService.LoadAsync(_disposeTokenSource.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));
        _appSettings = await loadTask;
        InitialiseProfiles();
        IsLoading = false;
        await RunRefreshLoopAsync();
    }

    private void InitialiseProfiles()
    {
        if (_appSettings.Profiles.Count == 0)
        {
            FanProfile defaultProfile = new() { Name = "Default" };
            _appSettings.Profiles.Add(defaultProfile);
            _appSettings.ActiveProfileId = defaultProfile.Id;
        }

        foreach (FanProfile profile in _appSettings.Profiles)
        {
            Profiles.Add(new ProfileTabViewModel(profile, OnSelectProfile, OnDeleteProfile, OnRenameProfile));
        }

        ProfileTabViewModel? activeTab = Profiles.FirstOrDefault(p => p.Profile.Id == _appSettings.ActiveProfileId)
                                         ?? Profiles.First();
        ActivateProfileTab(activeTab, applyToVm: true);
    }

    private void ActivateProfileTab(ProfileTabViewModel tab, bool applyToVm)
    {
        if (_activeProfileTab != null)
        {
            _activeProfileTab.IsActive = false;
        }

        _activeProfileTab = tab;
        tab.IsActive = true;
        _appSettings.ActiveProfileId = tab.Profile.Id;

        if (applyToVm)
        {
            ApplyProfileToVm(tab.Profile);
        }

        _ = SaveSettingsAsync();
    }

    private void ApplyProfileToVm(FanProfile profile)
    {
        _suppressProfileSync = true;
        try
        {
            CpuWarningThresholdDegrees = Math.Clamp(profile.CpuWarningThresholdDegrees, 50, 110);
            NotificationEmail = profile.NotificationEmail;
            SetCpuFanDesiredPercent(profile.CpuFanDesiredPercent);
        }
        finally
        {
            _suppressProfileSync = false;
        }
    }

    private void UpdateActiveProfileFromVm()
    {
        if (_activeProfileTab is null)
        {
            return;
        }

        FanProfile p = _activeProfileTab.Profile;
        p.CpuWarningThresholdDegrees = CpuWarningThresholdDegrees;
        p.NotificationEmail = NotificationEmail;
        p.CpuFanDesiredPercent = CpuFanDesiredPercent;
    }

    private void AddProfile()
    {
        // Snapshot current settings into the new profile.
        FanProfile newProfile = new()
        {
            Name = $"Profile {Profiles.Count + 1}",
            CpuWarningThresholdDegrees = CpuWarningThresholdDegrees,
            NotificationEmail = NotificationEmail,
            CpuFanDesiredPercent = CpuFanDesiredPercent,
        };

        _appSettings.Profiles.Add(newProfile);
        ProfileTabViewModel tab = new(newProfile, OnSelectProfile, OnDeleteProfile, OnRenameProfile);
        Profiles.Add(tab);
        ActivateProfileTab(tab, applyToVm: false);
    }

    private void OnSelectProfile(ProfileTabViewModel tab)
    {
        if (tab == _activeProfileTab)
        {
            return;
        }

        // Persist current VM state back to the outgoing profile before switching.
        UpdateActiveProfileFromVm();
        ActivateProfileTab(tab, applyToVm: true);
    }

    private void OnDeleteProfile(ProfileTabViewModel tab)
    {
        if (Profiles.Count <= 1)
        {
            return; // Always keep at least one profile.
        }

        int index = Profiles.IndexOf(tab);
        _appSettings.Profiles.Remove(tab.Profile);
        Profiles.Remove(tab);

        if (_activeProfileTab == tab)
        {
            ProfileTabViewModel next = Profiles[Math.Max(0, Math.Min(index, Profiles.Count - 1))];
            ActivateProfileTab(next, applyToVm: true);
        }
        else
        {
            _ = SaveSettingsAsync();
        }
    }

    private void OnRenameProfile(ProfileTabViewModel tab, string newName)
    {
        tab.Profile.Name = newName;
        tab.Name = newName;
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAsync(_appSettings, _disposeTokenSource.Token);
        }
        catch
        {
            // Best-effort — never crash the app for a settings write failure.
        }
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
                string emailNote = SmtpConfigured ? $" Alert email will be sent to {NotificationEmail}." : " Configure SMTP in the Notifications tab to receive alerts.";
                CpuTempWarningMessage = $"⚠  CPU temperature {hottest.Value:F0} °C exceeds the {CpuWarningThresholdDegrees:F0} °C threshold.{emailNote}";

                if (SmtpConfigured && DateTimeOffset.UtcNow - _lastAlertEmailSent > AlertEmailCooldown)
                {
                    _lastAlertEmailSent = DateTimeOffset.UtcNow;
                    _ = SendAlertEmailAsync(hottest.Value);
                }
            }
            else
            {
                HasCpuTempWarning = false;
                CpuTempWarningMessage = string.Empty;
            }

            SyncCpuReadings(snapshot.CpuReadings);
            SyncGpuReadings(snapshot.GpuReadings);
            SynchronizeFans(snapshot.Fans);

            // Feed live hardware data to the RGB effects engine.
            RgbControl.UpdateHardwareData(
                cpuTempC:   snapshot.CpuPackageTemperature ?? snapshot.CpuAverageTemperature,
                gpuTempC:   snapshot.GpuCoreTemperature,
                cpuLoadPct: snapshot.CpuTotalLoadPercent);
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

                // Restore stored desired percent from the active profile.
                if (_activeProfileTab?.Profile.FanChannelPercents.TryGetValue(snapshot.Id, out double stored) == true)
                {
                    channelViewModel.DesiredPercent = stored;
                }

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

        if (_activeProfileTab != null)
        {
            _activeProfileTab.Profile.FanChannelPercents[channel.Id] = channel.DesiredPercent;
            _ = SaveSettingsAsync();
        }

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

        if (!_suppressProfileSync && !_suppressCpuFanDesiredUpdate)
        {
            UpdateActiveProfileFromVm();
            _ = SaveSettingsAsync();
        }
    }

    partial void OnCpuWarningThresholdDegreesChanged(double value)
    {
        // Enforce the valid range in code, not just via the AXAML NumericUpDown.
        double clamped = Math.Clamp(value, 50, 110);
        if (clamped != value)
        {
            CpuWarningThresholdDegrees = clamped;
            return;
        }

        if (!_suppressProfileSync)
        {
            UpdateActiveProfileFromVm();
            _ = SaveSettingsAsync();
        }
    }

    partial void OnNotificationEmailChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            HasNotificationEmail = false;
        }
        else
        {
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

        UpdateSmtpConfigured();
            UpdateActiveProfileFromVm();
            _ = SaveSettingsAsync();
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

            if (_activeProfileTab != null)
            {
                _activeProfileTab.Profile.CpuFanDesiredPercent = CpuFanDesiredPercent;
                _ = SaveSettingsAsync();
            }

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

    partial void OnSmtpHostChanged(string value) => UpdateSmtpConfigured();
    partial void OnSmtpPortChanged(double value) => UpdateSmtpConfigured();
    partial void OnSmtpUserChanged(string value) => UpdateSmtpConfigured();
    partial void OnSmtpPasswordChanged(string value) => UpdateSmtpConfigured();

    private void UpdateSmtpConfigured()
    {
        SmtpConfigured = HasNotificationEmail
            && !string.IsNullOrWhiteSpace(SmtpHost)
            && !string.IsNullOrWhiteSpace(SmtpUser)
            && !string.IsNullOrWhiteSpace(SmtpPassword);
    }

    private async Task SendAlertEmailAsync(double tempC)
    {
        string subject = $"FANZI Alert: CPU temperature {tempC:F0} \u00b0C";
        string body = $"FANZI has detected that your CPU temperature ({tempC:F0} \u00b0C) " +
                      $"has exceeded the configured threshold of {CpuWarningThresholdDegrees:F0} \u00b0C.\r\n\r\n" +
                      $"System: {CpuName}\r\n" +
                      $"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\r\n\r\n" +
                      "This alert was sent by FANZI \u2014 Ionity Global (Pty) Ltd.";

        EmailStatus = await _emailService.SendAsync(SmtpHost, (int)SmtpPort, SmtpUser, SmtpPassword, NotificationEmail, subject, body);
    }

    private async Task SendTestEmailAsync()
    {
        if (!SmtpConfigured)
        {
            return;
        }

        IsSendingEmail = true;
        EmailStatus = "Sending test email\u2026";
        try
        {
            string subject = "FANZI \u2014 Test Notification";
            string body = "This is a test notification from FANZI \u2014 Fan Telemetry & Control.\r\n\r\n" +
                          "Your SMTP configuration is working correctly.\r\n\r\n" +
                          "\u00a9 2026 Ionity Global (Pty) Ltd.";

            EmailStatus = await _emailService.SendAsync(SmtpHost, (int)SmtpPort, SmtpUser, SmtpPassword, NotificationEmail, subject, body);
        }
        finally
        {
            IsSendingEmail = false;
        }
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
