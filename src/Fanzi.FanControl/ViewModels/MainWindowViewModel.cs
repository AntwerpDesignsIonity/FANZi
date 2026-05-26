using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.AI;
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
    private readonly IHardwareMonitorService _hardwareMonitorService;
    private readonly ISettingsService _settingsService;
    private readonly CancellationTokenSource _disposeTokenSource = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly EmailNotificationService _emailService = new();
    private readonly SmartFanCurveEngine _aiEngine = new();
    private readonly ProcessWatcher _processWatcher = new();

    private AppSettings _appSettings = new();
    private ProfileTabViewModel? _activeProfileTab;
    private string? _cpuFanChannelId;
    private bool _suppressCpuFanDesiredUpdate;
    private bool _suppressProfileSync;
    private bool _disposed;
    private bool _isWindowVisible = true;
    private DateTimeOffset _lastAlertEmailSent = DateTimeOffset.MinValue;
    private static readonly TimeSpan AlertEmailCooldown = TimeSpan.FromMinutes(10);

    public RgbControlViewModel RgbControl { get; }
    public TaskManagerViewModel TaskManagerVm { get; } = new();
    public NetworkManagerViewModel NetworkManagerVm { get; } = new();
    public PowerMonitorViewModel PowerMonitorVm { get; } = new();
    public SystemCleanerViewModel SystemCleanerVm { get; } = new();
    public SmartFanCurveEngine AiEngine => _aiEngine;

    // Section toggles bound to AppSettings
    [ObservableProperty] private bool _showTaskManagerTab = true;
    [ObservableProperty] private bool _showNetworkManagerTab = true;
    [ObservableProperty] private bool _showPowerMonitorTab = true;
    [ObservableProperty] private bool _showSystemCleanerTab = true;

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] private string _statusMessage = "Initializing hardware monitor...";
    [ObservableProperty] private string _cpuPackageTemperature = "--";
    [ObservableProperty] private string _cpuAverageTemperature = "--";
    [ObservableProperty] private string _cpuHotspotTemperature = "--";
    [ObservableProperty] private string _cpuTotalLoad = "--";
    [ObservableProperty] private string _cpuAverageClock = "--";
    [ObservableProperty] private string _cpuPackagePower = "--";
    [ObservableProperty] private string _cpuCoreVoltage = "--";
    [ObservableProperty] private string _gpuCoreTemperature = "--";
    [ObservableProperty] private string _gpuHotspotTemperature = "--";
    [ObservableProperty] private string _gpuLoad = "--";
    [ObservableProperty] private string _gpuClock = "--";
    [ObservableProperty] private string _gpuPower = "--";
    [ObservableProperty] private string _cpuName = string.Empty;
    [ObservableProperty] private string _gpuName = string.Empty;
    [ObservableProperty] private string _gpuVram = string.Empty;
    [ObservableProperty] private string _cpuFanName = "CPU fan";
    [ObservableProperty] private string _cpuFanSpeed = "Not detected";
    [ObservableProperty] private string _cpuFanControl = "--";
    [ObservableProperty] private string _cpuFanCapability = "Waiting for hardware sample...";
    [ObservableProperty] private bool _cpuFanCanControl;
    [ObservableProperty] private double _cpuFanDesiredPercent;
    [ObservableProperty] private string _cpuFanDesiredLabel = "--";
    [ObservableProperty] private bool _isCpuFanBusy;
    [ObservableProperty] private string _fanCountLabel = "0 channels";
    [ObservableProperty] private string _lastUpdated = "Waiting for first sample";
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _showHelp;
    [ObservableProperty] private double _cpuWarningThresholdDegrees = 95;
    [ObservableProperty] private bool _hasCpuTempWarning;
    [ObservableProperty] private string _cpuTempWarningMessage = string.Empty;
    [ObservableProperty] private string _notificationEmail = string.Empty;
    [ObservableProperty] private bool _hasNotificationEmail;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _smtpHost = string.Empty;
    [ObservableProperty] private double _smtpPort = 587;
    [ObservableProperty] private string _smtpUser = string.Empty;
    [ObservableProperty] private string _smtpPassword = string.Empty;
    [ObservableProperty] private bool _smtpConfigured;
    [ObservableProperty] private string _emailStatus = string.Empty;
    [ObservableProperty] private bool _isSendingEmail;

    // ── AI & Settings properties ──────────────────────────────────────────────

    [ObservableProperty] private bool _aiAutoFanEnabled;
    [ObservableProperty] private bool _aiAnomalyDetection = true;
    [ObservableProperty] private string _aiStatus = "AI Engine: Learning...";
    [ObservableProperty] private string _aiTrend = "Stable";
    [ObservableProperty] private string _aiPrediction = "--";
    [ObservableProperty] private string _aiSuggestedFan = "--";
    [ObservableProperty] private bool _hasAnomaly;
    [ObservableProperty] private string _anomalyMessage = string.Empty;
    [ObservableProperty] private string _anomalySeverity = "Normal";
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _closeToTray = true;

    // ── Collections ─────────���─────────────────────────────────────────────────

    public ObservableCollection<FanChannelViewModel> FanChannels { get; }
    public ObservableCollection<CpuReadingSnapshot> CpuReadings { get; }
    public ObservableCollection<GpuReadingSnapshot> GpuReadings { get; }
    public ObservableCollection<ProfileTabViewModel> Profiles { get; }

    // ── Commands ───────���──────────────────────────────────────────────────────

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ApplyCpuFanCommand { get; }
    public IAsyncRelayCommand AutoCpuFanCommand { get; }
    public IRelayCommand ToggleHelpCommand { get; }
    public IRelayCommand AddProfileCommand { get; }
    public IAsyncRelayCommand SendTestEmailCommand { get; }
    public IRelayCommand ShowWindowCommand { get; }
    public IRelayCommand ExitApplicationCommand { get; }

    // ── Constructor ────────────────────────────────────��──────────────────────

    public MainWindowViewModel(IHardwareMonitorService hardwareMonitorService, IRgbService rgbService, ISettingsService settingsService)
    {
        _hardwareMonitorService = hardwareMonitorService;
        _settingsService = settingsService;
        FanChannels = new ObservableCollection<FanChannelViewModel>();
        CpuReadings = new ObservableCollection<CpuReadingSnapshot>();
        GpuReadings = new ObservableCollection<GpuReadingSnapshot>();
        Profiles = new ObservableCollection<ProfileTabViewModel>();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApplyCpuFanCommand = new AsyncRelayCommand(ApplyCpuFanAsync);
        AutoCpuFanCommand = new AsyncRelayCommand(AutoCpuFanAsync);
        ToggleHelpCommand = new RelayCommand(() => ShowHelp = !ShowHelp);
        AddProfileCommand = new RelayCommand(AddProfile);
        SendTestEmailCommand = new AsyncRelayCommand(SendTestEmailAsync);
        ShowWindowCommand = new RelayCommand(ShowWindow);
        ExitApplicationCommand = new RelayCommand(ExitApplication);
        RgbControl = new RgbControlViewModel(rgbService);

        _ = RunStartupAsync();
    }

    // ── Lifecycle ────────────────────────────���────────────────────────────────

    public void SetWindowVisibility(bool visible)
    {
        _isWindowVisible = visible;
    }

    private void ShowWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Show();
            desktop.MainWindow!.WindowState = Avalonia.Controls.WindowState.Normal;
            desktop.MainWindow.Activate();
        }
    }

    private void ExitApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeTokenSource.Cancel();
        _refreshLock.Dispose();
        _disposeTokenSource.Dispose();
        RgbControl.Dispose();
        TaskManagerVm.Dispose();
        NetworkManagerVm.Dispose();
        PowerMonitorVm.Dispose();
        SystemCleanerVm.Dispose();
        foreach (var fanChannel in FanChannels)
            fanChannel.Dispose();
    }

    // ── Startup ────────���──────────────────────────────────────────────────────

    private async Task RunStartupAsync()
    {
        Task<AppSettings> loadTask = _settingsService.LoadAsync(_disposeTokenSource.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        _appSettings = await loadTask;
        ApplySettingsToVm();
        InitialiseProfiles();
        IsLoading = false;
        await RunRefreshLoopAsync();
    }

    private void ApplySettingsToVm()
    {
        _suppressProfileSync = true;
        try
        {
            StartWithWindows = _appSettings.StartWithWindows;
            StartMinimized = _appSettings.StartMinimized;
            MinimizeToTray = _appSettings.MinimizeToTray;
            CloseToTray = _appSettings.CloseToTray;
            AiAutoFanEnabled = _appSettings.AiAutoFanEnabled;
            AiAnomalyDetection = _appSettings.AiAnomalyDetection;
            ShowTaskManagerTab = _appSettings.ShowTaskManagerTab;
            ShowNetworkManagerTab = _appSettings.ShowNetworkManagerTab;
            ShowPowerMonitorTab = _appSettings.ShowPowerMonitorTab;
            ShowSystemCleanerTab = _appSettings.ShowSystemCleanerTab;
        }
        finally
        {
            _suppressProfileSync = false;
        }
    }

    // ── Settings change handlers ──────────────────────────────────────────────

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.StartWithWindows = value;
        StartupService.SetStartup(value);
        _ = SaveSettingsAsync();
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.StartMinimized = value;
        _ = SaveSettingsAsync();
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.MinimizeToTray = value;
        _ = SaveSettingsAsync();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.CloseToTray = value;
        _ = SaveSettingsAsync();
    }

    partial void OnShowTaskManagerTabChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.ShowTaskManagerTab = value;
        _ = SaveSettingsAsync();
    }

    partial void OnShowNetworkManagerTabChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.ShowNetworkManagerTab = value;
        _ = SaveSettingsAsync();
    }

    partial void OnShowPowerMonitorTabChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.ShowPowerMonitorTab = value;
        _ = SaveSettingsAsync();
    }

    partial void OnShowSystemCleanerTabChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.ShowSystemCleanerTab = value;
        _ = SaveSettingsAsync();
    }

    partial void OnAiAutoFanEnabledChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.AiAutoFanEnabled = value;
        if (!value) _aiEngine.Reset();
        _ = SaveSettingsAsync();
    }

    partial void OnAiAnomalyDetectionChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.AiAnomalyDetection = value;
        _ = SaveSettingsAsync();
    }

    // ── Profiles ──────────��───────────────────────────────────────────────────

    private void InitialiseProfiles()
    {
        if (_appSettings.Profiles.Count == 0)
        {
            FanProfile defaultProfile = new() { Name = "Default" };
            _appSettings.Profiles.Add(defaultProfile);
            _appSettings.ActiveProfileId = defaultProfile.Id;
        }

        foreach (var profile in _appSettings.Profiles)
            Profiles.Add(new ProfileTabViewModel(profile, OnSelectProfile, OnDeleteProfile, OnRenameProfile));

        var activeTab = Profiles.FirstOrDefault(p => p.Profile.Id == _appSettings.ActiveProfileId)
                        ?? Profiles.First();
        ActivateProfileTab(activeTab, applyToVm: true);
    }

    private void ActivateProfileTab(ProfileTabViewModel tab, bool applyToVm)
    {
        if (_activeProfileTab != null)
            _activeProfileTab.IsActive = false;

        _activeProfileTab = tab;
        tab.IsActive = true;
        _appSettings.ActiveProfileId = tab.Profile.Id;

        if (applyToVm)
            ApplyProfileToVm(tab.Profile);

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
        if (_activeProfileTab is null) return;
        var p = _activeProfileTab.Profile;
        p.CpuWarningThresholdDegrees = CpuWarningThresholdDegrees;
        p.NotificationEmail = NotificationEmail;
        p.CpuFanDesiredPercent = CpuFanDesiredPercent;
    }

    private void AddProfile()
    {
        FanProfile newProfile = new()
        {
            Name = $"Profile {Profiles.Count + 1}",
            CpuWarningThresholdDegrees = CpuWarningThresholdDegrees,
            NotificationEmail = NotificationEmail,
            CpuFanDesiredPercent = CpuFanDesiredPercent,
            FanCurve = SmartFanCurveEngine.GenerateDefaultCurve(),
        };

        _appSettings.Profiles.Add(newProfile);
        var tab = new ProfileTabViewModel(newProfile, OnSelectProfile, OnDeleteProfile, OnRenameProfile);
        Profiles.Add(tab);
        ActivateProfileTab(tab, applyToVm: false);
    }

    private void OnSelectProfile(ProfileTabViewModel tab)
    {
        if (tab == _activeProfileTab) return;
        UpdateActiveProfileFromVm();
        ActivateProfileTab(tab, applyToVm: true);
    }

    private void OnDeleteProfile(ProfileTabViewModel tab)
    {
        if (Profiles.Count <= 1) return;
        int index = Profiles.IndexOf(tab);
        _appSettings.Profiles.Remove(tab.Profile);
        Profiles.Remove(tab);

        if (_activeProfileTab == tab)
        {
            var next = Profiles[Math.Max(0, Math.Min(index, Profiles.Count - 1))];
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
        try { await _settingsService.SaveAsync(_appSettings, _disposeTokenSource.Token); }
        catch { }
    }

    // ── Refresh loop with adaptive polling ────────────────────────────────────

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            while (!_disposeTokenSource.IsCancellationRequested)
            {
                await RefreshAsync();

                int intervalMs = _isWindowVisible
                    ? _appSettings.PollingIntervalSeconds * 1000
                    : _appSettings.ReducedPollingIntervalSeconds * 1000;

                await Task.Delay(intervalMs, _disposeTokenSource.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        if (!await _refreshLock.WaitAsync(0)) return;

        try
        {
            IsRefreshing = true;
            var snapshot = await _hardwareMonitorService.GetSnapshotAsync(_disposeTokenSource.Token);

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
            // Friendly label: prefix with PUMP/AIO when CPU cooling is liquid-cooled
            if (snapshot.CpuFan is { } cf)
            {
                string kindPrefix = cf.DeviceKind switch
                {
                    FanDeviceKind.Pump      => "CPU Pump",
                    FanDeviceKind.AioCooler => "CPU AIO Cooler",
                    _ => "CPU Fan",
                };
                CpuFanName = $"{kindPrefix} · {cf.Name}";
            }
            else
            {
                CpuFanName = "CPU Cooling — not detected";
            }
            CpuFanSpeed = snapshot.CpuFan?.SpeedRpm is double rpm
                ? $"{rpm:F0} RPM"
                : "No CPU fan/pump exposed on this motherboard";
            CpuFanControl = snapshot.CpuFan?.CurrentControlPercent is double control ? $"{control:F0}%" : "Auto/BIOS";
            CpuFanCapability = snapshot.CpuFan?.CapabilityMessage ?? "No dedicated CPU fan header was detected.";
            CpuFanCanControl = snapshot.CpuFan?.CanControl == true;
            _cpuFanChannelId = snapshot.CpuFan?.Id;

            if (snapshot.CpuFan?.CurrentControlPercent is double cpuFanPercent)
                SetCpuFanDesiredPercent(cpuFanPercent);

            FanCountLabel = snapshot.Fans.Count == 1 ? "1 channel" : $"{snapshot.Fans.Count} channels";
            LastUpdated = $"Updated {snapshot.Timestamp.LocalDateTime:HH:mm:ss}";
            StatusMessage = snapshot.StatusMessage;

            // ── AI Engine update ──────────────────────────────────────
            double? hottest = snapshot.CpuHotspotTemperature ?? snapshot.CpuPackageTemperature ?? snapshot.CpuAverageTemperature;
            double cpuLoad = snapshot.CpuTotalLoadPercent ?? 0;

            if (hottest.HasValue)
            {
                double suggested = _aiEngine.ComputeOptimalFanSpeed(
                    hottest.Value, cpuLoad, snapshot.CpuFan?.SpeedRpm,
                    _activeProfileTab?.Profile.FanCurve);

                AiSuggestedFan = $"{suggested:F0}%";
                AiTrend = _aiEngine.Predictor.Trend.ToString();
                AiPrediction = _aiEngine.Predictor.PredictedTempIn30s.HasValue
                    ? $"{_aiEngine.Predictor.PredictedTempIn30s.Value:F1}C in 30s"
                    : "Learning...";
                AiStatus = _aiEngine.IsLearning ? "AI Engine: Active" : "AI Engine: Learning...";

                if (AiAutoFanEnabled && CpuFanCanControl && !string.IsNullOrEmpty(_cpuFanChannelId))
                {
                    await _hardwareMonitorService.SetFanControlAsync(_cpuFanChannelId, suggested, _disposeTokenSource.Token);
                }

                // Anomaly detection
                if (AiAnomalyDetection)
                {
                    var report = _aiEngine.AnomalyDetector.LastReport;
                    if (report is not null)
                    {
                        HasAnomaly = true;
                        AnomalySeverity = report.Severity.ToString();
                        AnomalyMessage = string.Join(" | ", report.Anomalies);
                    }
                    else
                    {
                        HasAnomaly = false;
                        AnomalyMessage = string.Empty;
                    }
                }
            }

            // ── Temperature warning check ─────────────────────────────
            if (hottest.HasValue && hottest.Value >= CpuWarningThresholdDegrees)
            {
                HasCpuTempWarning = true;
                CpuTempWarningMessage = $"CPU temperature {hottest.Value:F0}C exceeds {CpuWarningThresholdDegrees:F0}C threshold.";

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

            RgbControl.UpdateHardwareData(
                cpuTempC: snapshot.CpuPackageTemperature ?? snapshot.CpuAverageTemperature,
                gpuTempC: snapshot.GpuCoreTemperature,
                cpuLoadPct: snapshot.CpuTotalLoadPercent);
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "Hardware poll failed: access denied. Run as Administrator.";
        }
        catch
        {
            StatusMessage = "Hardware poll encountered an error.";
        }
        finally
        {
            IsRefreshing = false;
            _refreshLock.Release();
        }
    }

    // ── Fan synchronization ───────────────────────────────────────────────────

    private void SynchronizeFans(IReadOnlyList<FanChannelSnapshot> fans)
    {
        var existing = FanChannels.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in fans)
        {
            if (!existing.TryGetValue(snapshot.Id, out var vm))
            {
                vm = new FanChannelViewModel(snapshot, ApplyFanControlAsync, RestoreAutomaticControlAsync);
                if (_activeProfileTab?.Profile.FanChannelPercents.TryGetValue(snapshot.Id, out double stored) == true)
                    vm.DesiredPercent = stored;
                FanChannels.Add(vm);
                continue;
            }
            vm.Update(snapshot);
            existing.Remove(snapshot.Id);
        }

        foreach (var (_, vm) in existing)
        {
            vm.Dispose();
            FanChannels.Remove(vm);
        }
    }

    private void SyncCpuReadings(IReadOnlyList<CpuReadingSnapshot> readings)
    {
        CpuReadings.Clear();
        foreach (var r in readings) CpuReadings.Add(r);
    }

    private void SyncGpuReadings(IReadOnlyList<GpuReadingSnapshot> readings)
    {
        GpuReadings.Clear();
        foreach (var r in readings) GpuReadings.Add(r);
    }

    // ── Fan control commands ──────────────────────────────────────────────────

    private async Task ApplyFanControlAsync(FanChannelViewModel channel)
    {
        var result = await _hardwareMonitorService.SetFanControlAsync(channel.Id, channel.DesiredPercent, _disposeTokenSource.Token);
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
        var result = await _hardwareMonitorService.RestoreAutomaticControlAsync(channel.Id, _disposeTokenSource.Token);
        channel.ApplyResult(result);
        await RefreshAsync();
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
            var result = await _hardwareMonitorService.SetFanControlAsync(_cpuFanChannelId, CpuFanDesiredPercent, _disposeTokenSource.Token);
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
            var result = await _hardwareMonitorService.RestoreAutomaticControlAsync(_cpuFanChannelId, _disposeTokenSource.Token);
            CpuFanCapability = result.Message;
            await RefreshAsync();
        }
        finally
        {
            IsCpuFanBusy = false;
        }
    }

    // ── Property change handlers ───���──────────────────────────────────────────

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
        double clamped = Math.Clamp(value, 50, 110);
        if (clamped != value) { CpuWarningThresholdDegrees = clamped; return; }
        if (!_suppressProfileSync) { UpdateActiveProfileFromVm(); _ = SaveSettingsAsync(); }
    }

    partial void OnNotificationEmailChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            HasNotificationEmail = false;
        }
        else
        {
            try { _ = new MailAddress(value.Trim()); HasNotificationEmail = true; }
            catch (FormatException) { HasNotificationEmail = false; }

            UpdateSmtpConfigured();
            UpdateActiveProfileFromVm();
            _ = SaveSettingsAsync();
        }
    }

    private void SetCpuFanDesiredPercent(double value)
    {
        if (_suppressCpuFanDesiredUpdate) return;
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

    // ── Email ────���────────────────────────────────────────────────────────────

    private async Task SendAlertEmailAsync(double tempC)
    {
        string subject = $"FANZI Alert: CPU temperature {tempC:F0} °C";
        string body = $"FANZI has detected that your CPU temperature ({tempC:F0} °C) " +
                      $"exceeded the threshold of {CpuWarningThresholdDegrees:F0} °C.\r\n\r\n" +
                      $"AI Trend: {AiTrend}\r\nPrediction: {AiPrediction}\r\n" +
                      $"System: {CpuName}\r\nTimestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\r\n\r\n" +
                      "Sent by FANZI — Ionity Global (Pty) Ltd.";

        EmailStatus = await _emailService.SendAsync(SmtpHost, (int)SmtpPort, SmtpUser, SmtpPassword, NotificationEmail, subject, body);
    }

    private async Task SendTestEmailAsync()
    {
        if (!SmtpConfigured) return;
        IsSendingEmail = true;
        EmailStatus = "Sending test email...";
        try
        {
            string subject = "FANZI — Test Notification";
            string body = "This is a test notification from FANZI.\r\n\r\n" +
                          "Your SMTP configuration is working correctly.\r\n\r\n" +
                          "© 2026 Ionity Global (Pty) Ltd.";
            EmailStatus = await _emailService.SendAsync(SmtpHost, (int)SmtpPort, SmtpUser, SmtpPassword, NotificationEmail, subject, body);
        }
        finally
        {
            IsSendingEmail = false;
        }
    }

    // ── Formatters ───────────────────────────────────────���────────────────────

    private static string FormatTemperature(double? t) => t.HasValue ? $"{t.Value:F1} C" : "--";
    private static string FormatPercent(double? v) => v.HasValue ? $"{v.Value:F0}%" : "--";
    private static string FormatClock(double? v) => v.HasValue ? $"{v.Value:F0} MHz" : "--";
    private static string FormatPower(double? v) => v.HasValue ? $"{v.Value:F1} W" : "--";
    private static string FormatVoltage(double? v) => v.HasValue ? $"{v.Value:F3} V" : "--";

    private static string FormatVram(double? usedMb, double? totalMb)
    {
        if (!usedMb.HasValue && !totalMb.HasValue) return string.Empty;
        if (usedMb.HasValue && totalMb.HasValue) return $"{usedMb.Value / 1024.0:F1} / {totalMb.Value / 1024.0:F1} GB";
        if (totalMb.HasValue) return $"{totalMb.Value / 1024.0:F1} GB";
        return $"{usedMb!.Value / 1024.0:F1} GB used";
    }
}
