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
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.ViewModels;

[SupportedOSPlatform("windows")]
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IHardwareMonitorService _hardwareMonitorService;
    private readonly ISettingsService _settingsService;
    private readonly CancellationTokenSource _disposeTokenSource = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly EmailNotificationService _emailService = new();
    private readonly SmartFanCurveEngine _aiEngine = new();
    private readonly ThermalCoach _thermalCoach = new();
    private readonly GameModeDetector _gameModeDetector;
    private readonly SystemHealthScorer _healthScorer = new();
    private readonly AediKnowledgeEngine _aediEngine = new();
    private readonly GoogleProfileService _googleProfile = new();

    private AppSettings _appSettings = new();
    private ProfileTabViewModel? _activeProfileTab;
    private string? _cpuFanChannelId;
    private bool _suppressCpuFanDesiredUpdate;
    private bool _suppressProfileSync;
    private bool _disposed;
    private bool _isWindowVisible = true;
    private DateTimeOffset _lastAlertEmailSent = DateTimeOffset.MinValue;
    private static readonly TimeSpan AlertEmailCooldown = TimeSpan.FromMinutes(10);

    // While the user drags the CPU fan slider, hardware readings must not snap it back.
    private DateTime _lastUserSliderChangeUtc = DateTime.MinValue;
    private static readonly TimeSpan SliderSyncHoldOff = TimeSpan.FromSeconds(5);

    // AI auto-fan only writes to the hardware when its target moves by at least 1 %.
    private double? _lastAiAppliedPercent;
    private string? _lastAiAppliedChannelId;

    // Per-poll errors are logged once per distinct error per minute, not every poll.
    private string _lastPollErrorSignature = string.Empty;
    private DateTime _lastPollErrorLoggedUtc = DateTime.MinValue;
    private static readonly TimeSpan PollErrorLogCooldown = TimeSpan.FromMinutes(1);

    /// <summary>Raised on the UI thread once settings are loaded and applied (App applies StartMinimized).</summary>
    public event EventHandler? SettingsLoaded;

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

    // Numeric twins of the strings above — drive gauges and temperature-reactive colours.
    [ObservableProperty] private double? _cpuPackageTempValue;
    [ObservableProperty] private double? _cpuAverageTempValue;
    [ObservableProperty] private double? _cpuHotspotTempValue;
    [ObservableProperty] private double? _gpuCoreTempValue;
    [ObservableProperty] private double? _gpuHotspotTempValue;
    [ObservableProperty] private double? _hottestTempValue;
    /// <summary>Hero/overlay CPU gauge value: package → hotspot → average core (null shows "--").</summary>
    [ObservableProperty] private double? _cpuGaugeTempValue;
    [ObservableProperty] private double _cpuLoadValue;
    [ObservableProperty] private double _gpuLoadValue;
    [ObservableProperty] private double _healthScoreValue = 100;
    [ObservableProperty] private double _cpuFanRpmValue;

    /// <summary>Tray icon tooltip, refreshed every poll (see <see cref="TrayTooltipFormatter"/>).</summary>
    [ObservableProperty] private string _trayTooltip = TrayTooltipFormatter.Default;

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
    [ObservableProperty] private string _aiStatus = "AEDi: Learning...";
    [ObservableProperty] private string _aiTrend = "Stable";
    [ObservableProperty] private string _aiPrediction = "--";
    [ObservableProperty] private string _aiForecast = "--";
    [ObservableProperty] private string _aiSuggestedFan = "--";
    [ObservableProperty] private bool _hasAnomaly;
    [ObservableProperty] private string _anomalyMessage = string.Empty;
    [ObservableProperty] private string _anomalySeverity = "Normal";
    // ── IO-nity AI properties ──────────────────────────────────────────────

    [ObservableProperty] private string _healthScore = "100";
    [ObservableProperty] private string _healthGrade = "A+";
    [ObservableProperty] private string _healthEmoji = "🟢";
    [ObservableProperty] private string _healthSummary = "System Health: 100/100 (A+)";
    [ObservableProperty] private string _healthInsights = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GameModeTitle))] private bool _isGameMode;
    /// <summary>Hero tile title: short and static so it never trims; details go to <see cref="GameModeDetail"/>.</summary>
    public string GameModeTitle => IsGameMode ? "🎮 Game Mode" : "Desktop mode";
    [ObservableProperty] private string _gameModeStatus = "Desktop mode";
    [ObservableProperty] private string _detectedGame = "";
    [ObservableProperty] private string _gameModeDetail = "";
    [ObservableProperty] private string _coachRecommendations = "";
    [ObservableProperty] private bool _hasCoachRecommendations;
    [ObservableProperty] private string _ionityVersion = $"IO-nity {AppVersion.ShortLabel}";

    // ── Google Profile ────────────────────────────────────────────────────

    [ObservableProperty] private bool _isLoggedInGoogle;
    [ObservableProperty] private string _googleUserName = "";
    [ObservableProperty] private string _googleUserEmail = "";
    [ObservableProperty] private string _googleStatus = "Not signed in";
    [ObservableProperty] private string _googleSyncStatus = "";

    // ── AEDi Search ───────────────────────────────────────────────────────

    [ObservableProperty] private string _aediQuery = "";
    [ObservableProperty] private string _aediResults = "";
    [ObservableProperty] private bool _hasAediResults;
    [ObservableProperty] private string _aediWelcome = "Hi! I'm AEDi — your FANZi assistant. Search or type a command like 'fans', 'rgb', 'clean ram', 'port scan'...";
    [ObservableProperty] private int _aediNavigateTab = -1;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _startupStatus = string.Empty;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _overlayTransparent = false;
    [ObservableProperty] private double _overlayOpacity = 0.90;
    [ObservableProperty] private bool _overlayTopmost;

    /// <summary>Index of the RGB tab in the main TabControl (Dashboard, Fans, Sensors, RGB, …).</summary>
    internal const int RgbTabIndex = 3;

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
    public IRelayCommand PinToTaskbarCommand { get; }
    public IRelayCommand PinToStartCommand { get; }
    public IRelayCommand CreateDesktopShortcutCommand { get; }
    public IRelayCommand OpenProfilesFolderCommand { get; }
    public IAsyncRelayCommand SaveCurrentProfileCommand { get; }
    public IAsyncRelayCommand RefreshProfileListCommand { get; }
    public IRelayCommand<ProfileFileInfo> LoadProfileFromDiskCommand { get; }
    public IRelayCommand<ProfileFileInfo> DeleteProfileFromDiskCommand { get; }
    public IRelayCommand ToggleMiniOverlayCommand { get; }
    public IRelayCommand OpenRgbTabCommand { get; }
    public IRelayCommand ExportHardwareReportCommand { get; }
    public IAsyncRelayCommand GoogleLoginCommand { get; }
    public IRelayCommand GoogleLogoutCommand { get; }
    public IAsyncRelayCommand GoogleSyncUpCommand { get; }
    public IAsyncRelayCommand GoogleSyncDownCommand { get; }
    public IRelayCommand AediSearchCommand { get; }
    public IRelayCommand<object> AediNavigateCommand { get; }

    // Profile manager + pinning state
    private readonly ProfileManagerService _profileMgr = new();
    public ObservableCollection<ProfileFileInfo> DiskProfiles { get; } = new();
    [ObservableProperty] private string _profileSaveStatus = "";
    [ObservableProperty] private string _pinningStatus = "";
    [ObservableProperty] private string _hardwareReportStatus = "";
    [ObservableProperty] private bool _miniOverlayVisible;
    public string ProfilesDirectoryPath => ProfileManagerService.DefaultProfileDir;

    // Mini overlay window — created lazily
    private Avalonia.Controls.Window? _miniOverlay;

    // ── Constructor ────────────────────────────────────��──────────────────────

    public MainWindowViewModel(IHardwareMonitorService hardwareMonitorService, IRgbService rgbService, ISettingsService settingsService)
        : this(hardwareMonitorService, settingsService, new RgbControlViewModel(rgbService))
    {
        StartUpdateChecks();
    }

    /// <param name="rgbControl">RGB tab view model; tests/render harness pass one without background
    /// work so no OpenRGB detection, download or server start happens.</param>
    /// <param name="gameModeDetector">Optional detector with a faked process scan (render harness);
    /// defaults to the live process-list detector.</param>
    internal MainWindowViewModel(IHardwareMonitorService hardwareMonitorService, ISettingsService settingsService, RgbControlViewModel rgbControl,
        GameModeDetector? gameModeDetector = null)
    {
        _gameModeDetector = gameModeDetector ?? new GameModeDetector();
        _hardwareMonitorService = hardwareMonitorService;
        _settingsService = settingsService;
        FanChannels = new ObservableCollection<FanChannelViewModel>();
        CpuReadings = new ObservableCollection<CpuReadingSnapshot>();
        GpuReadings = new ObservableCollection<GpuReadingSnapshot>();
        Profiles = new ObservableCollection<ProfileTabViewModel>();
        RefreshCommand = new AsyncRelayCommand(() => SafeExecuteAsync(RefreshAsync, "Refresh"));
        ApplyCpuFanCommand = new AsyncRelayCommand(() => SafeExecuteAsync(ApplyCpuFanAsync, "Apply CPU fan"));
        AutoCpuFanCommand = new AsyncRelayCommand(() => SafeExecuteAsync(AutoCpuFanAsync, "CPU fan auto mode"));
        ToggleHelpCommand = new RelayCommand(() => ShowHelp = !ShowHelp);
        AddProfileCommand = new RelayCommand(AddProfile);
        SendTestEmailCommand = new AsyncRelayCommand(() => SafeExecuteAsync(SendTestEmailAsync, "Test email"));
        ShowWindowCommand = new RelayCommand(ShowWindow);
        ExitApplicationCommand = new RelayCommand(ExitApplication);

        PinToTaskbarCommand = new RelayCommand(PinToTaskbar);
        PinToStartCommand = new RelayCommand(PinToStart);
        CreateDesktopShortcutCommand = new RelayCommand(CreateDesktopShortcut);
        OpenProfilesFolderCommand = new RelayCommand(() => _profileMgr.OpenProfilesFolder());
        SaveCurrentProfileCommand = new AsyncRelayCommand(() => SafeExecuteAsync(SaveCurrentProfileToDiskAsync, "Save profile"));
        RefreshProfileListCommand = new AsyncRelayCommand(() => SafeExecuteAsync(RefreshDiskProfilesAsync, "Refresh profile list"));
        LoadProfileFromDiskCommand = new RelayCommand<ProfileFileInfo>(LoadProfileFromDisk);
        DeleteProfileFromDiskCommand = new RelayCommand<ProfileFileInfo>(DeleteProfileFromDisk);
        ToggleMiniOverlayCommand = new RelayCommand(ToggleMiniOverlay);
        InitializeOverlayStudio();
        OpenRgbTabCommand = new RelayCommand(() => SelectedTabIndex = RgbTabIndex);
        ExportHardwareReportCommand = new RelayCommand(ExportHardwareReport);
        GoogleLoginCommand = new AsyncRelayCommand(() => SafeExecuteAsync(GoogleLoginAsync, "Google sign-in"));
        GoogleLogoutCommand = new RelayCommand(GoogleLogout);
        GoogleSyncUpCommand = new AsyncRelayCommand(() => SafeExecuteAsync(GoogleSyncUpAsync, "Google sync up"));
        GoogleSyncDownCommand = new AsyncRelayCommand(() => SafeExecuteAsync(GoogleSyncDownAsync, "Google sync down"));
        AediSearchCommand = new RelayCommand(AediSearch);
        AediNavigateCommand = new RelayCommand<object>(AediNavigate);

        RgbControl = rgbControl;
        WireAlerts();
        _ = SafeExecuteAsync(RefreshDiskProfilesAsync, "Refresh profile list");

        _ = RunStartupAsync();
    }

    /// <summary>
    /// Runs an async action so that a failure is logged and shown in the status bar instead of
    /// escaping to the dispatcher (which would crash the app). Cancellation during shutdown is silent.
    /// </summary>
    internal async Task SafeExecuteAsync(Func<Task> action, string context)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error($"{context} failed", ex);
            if (!_disposed)
                StatusMessage = $"{context} failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── Lifecycle ────────────────────────────���────────────────────────────────

    public void SetWindowVisibility(bool visible)
    {
        _isWindowVisible = visible;
        // Back on screen: the tray alert has been seen (or is stale), drop it from the tooltip.
        if (visible) ClearTrayAlert();
    }

    private void ShowWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var window = desktop.MainWindow;
        if (window is null)
            return;

        try
        {
            window.Show();
            window.WindowState = Avalonia.Controls.WindowState.Normal;
            window.Activate();
        }
        catch (InvalidOperationException ex)
        {
            // e.g. the window was already closed during shutdown.
            AppLog.Warn("Show window ignored", ex);
        }
    }

    private void ExitApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    // ── Pinning / shortcuts ──────────────────────────────────────────────────

    private void PinToTaskbar()
    {
        if (!OperatingSystem.IsWindows()) { PinningStatus = "Windows only"; return; }
        string exe = PinningService.GetCurrentExePath();
        bool ok = PinningService.PinToTaskbar(exe);
        PinningStatus = ok
            ? "Pinned to Taskbar. If Windows 11 didn't accept the pin, right-click the new Start entry → Pin to taskbar."
            : "Auto-pin not allowed on this Windows build — open the new Start entry, right-click, and pick 'Pin to taskbar'.";
    }

    private void PinToStart()
    {
        if (!OperatingSystem.IsWindows()) { PinningStatus = "Windows only"; return; }
        string exe = PinningService.GetCurrentExePath();
        bool ok = PinningService.PinToStartMenu(exe);
        PinningStatus = ok
            ? "Added to Start menu (search 'FANZI' or open All Apps)."
            : "Could not write Start menu shortcut";
    }

    private void CreateDesktopShortcut()
    {
        if (!OperatingSystem.IsWindows()) { PinningStatus = "Windows only"; return; }
        string exe = PinningService.GetCurrentExePath();
        bool ok = PinningService.CreateDesktopShortcut(exe);
        PinningStatus = ok ? "Desktop shortcut created" : "Could not create desktop shortcut";
    }

    // ── Profile manager (disk) ───────────────────────────────────────────────

    private async Task RefreshDiskProfilesAsync()
    {
        var list = await _profileMgr.ListAsync(_disposeTokenSource.Token);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DiskProfiles.Clear();
            foreach (var p in list) DiskProfiles.Add(p);
        });
    }

    private async Task SaveCurrentProfileToDiskAsync()
    {
        var active = _activeProfileTab?.Profile;
        if (active is null) { ProfileSaveStatus = "No active profile to save"; return; }
        try
        {
            string path = await _profileMgr.SaveAsync(active, _disposeTokenSource.Token);
            ProfileSaveStatus = $"Saved → {path}";
            RaiseAlert(AppAlertKind.ProfileSaved, AppAlertLevel.Success, "profile:saved:" + active.Name, "Profile saved",
                $"'{active.Name}' was saved to the profile library.");
            await RefreshDiskProfilesAsync();
        }
        catch (Exception ex)
        {
            ProfileSaveStatus = $"Save failed: {ex.Message}";
        }
    }

    private void LoadProfileFromDisk(ProfileFileInfo? info)
    {
        if (info is null) return;
        _ = SafeExecuteAsync(() => LoadProfileFromDiskAsync(info), "Load profile");
    }

    private async Task LoadProfileFromDiskAsync(ProfileFileInfo info)
    {
        var loaded = await _profileMgr.LoadFromPathAsync(info.FullPath, _disposeTokenSource.Token);
        if (loaded is null)
        {
            ProfileSaveStatus = $"Failed to load {info.Name}";
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Register with AppSettings and create a tab using the same wiring as built-ins
            _appSettings.Profiles.Add(loaded);
            var tab = new ProfileTabViewModel(loaded, OnSelectProfile, OnDeleteProfile, OnRenameProfile);
            Profiles.Add(tab);
            ActivateProfileTab(tab, applyToVm: true);
            ProfileSaveStatus = $"Loaded '{loaded.Name}' from disk";
            RaiseAlert(AppAlertKind.ProfileLoaded, AppAlertLevel.Information, "profile:loaded:" + loaded.Name, "Profile loaded",
                $"'{loaded.Name}' is now the active profile.");
        });
    }

    private void DeleteProfileFromDisk(ProfileFileInfo? info)
    {
        if (info is null) return;
        if (_profileMgr.Delete(info.FullPath))
        {
            DiskProfiles.Remove(info);
            ProfileSaveStatus = $"Deleted {info.Name}";
        }
        else ProfileSaveStatus = "Delete failed";
    }

    // ── Mini overlay window ──────────────────────────────────────────────────

    private void ToggleMiniOverlay()
    {
        // Tray / shortcut / button can arrive off the UI thread on some platforms: windows are UI-only.
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ToggleMiniOverlay);
            return;
        }

        try
        {
            if (_miniOverlay is not null && _miniOverlay.IsVisible)
            {
                HideMiniOverlay();
                return;
            }
            ShowMiniOverlay();
        }
        catch (Exception ex)
        {
            // Never fail silently: v3 users saw "nothing happens" — log it and say so in Settings.
            AppLog.Error("Showing the mini overlay failed", ex);
            OverlayStatus = $"Overlay could not open: {ex.Message}";
            StatusMessage = OverlayStatus;
            try { _miniOverlay?.Close(); } catch { /* already broken */ }
            _miniOverlay = null;
            MiniOverlayVisible = false;
        }
    }

    /// <summary>Creates (first time) and shows the overlay, always on a visible screen and above other windows.</summary>
    private void ShowMiniOverlay()
    {
        if (_miniOverlay is null)
        {
            var overlay = new Views.MiniOverlayWindow { DataContext = this };
            overlay.Closed += (_, _) =>
            {
                if (ReferenceEquals(_miniOverlay, overlay)) _miniOverlay = null;
                MiniOverlayVisible = false;
            };
            _miniOverlay = overlay;
        }

        _miniOverlay.Show();
        if (_miniOverlay is Views.MiniOverlayWindow mini) mini.BringIntoView();
        MiniOverlayVisible = true;
        OverlayStatus = "Overlay visible — drag it anywhere, right-click for options (Ctrl+O hides it)";
        AppLog.Info("Mini overlay shown");
    }

    /// <summary>Hides the overlay (✕ button, context menu, tray toggle) and keeps MiniOverlayVisible in sync.</summary>
    public void HideMiniOverlay()
    {
        _miniOverlay?.Hide();
        MiniOverlayVisible = false;
        OverlayStatus = "Overlay hidden — Ctrl+O or the ▣ title-bar button shows it again";
    }

    /// <summary>Saved overlay position in screen pixels, or null when never moved.</summary>
    public int? OverlayX => _appSettings.OverlayX;
    public int? OverlayY => _appSettings.OverlayY;

    /// <summary>Persists the overlay position (called debounced by the overlay window).</summary>
    public void SaveOverlayPosition(int x, int y)
    {
        if (_disposed || (_appSettings.OverlayX == x && _appSettings.OverlayY == y)) return;
        _appSettings.OverlayX = x;
        _appSettings.OverlayY = y;
        _ = SaveSettingsAsync();
    }

    // ── Hardware report export ───────────────────────────────────────────────

    private void ExportHardwareReport()
    {
        try
        {
            using var svc = new HardwareReportService();
            string path = svc.SaveToDefaultLocation();
            HardwareReportStatus = $"Saved → {path}";
            // Open in default editor
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            catch { }
        }
        catch (Exception ex)
        {
            HardwareReportStatus = $"Export failed: {ex.Message}";
        }
    }

    // ── Google Profile ────────────────────────────────────────────────────

    private async Task GoogleLoginAsync()
    {
        GoogleStatus = "Signing in to Google...";
        bool ok = await _googleProfile.LoginAsync(_disposeTokenSource.Token);
        IsLoggedInGoogle = ok;
        GoogleUserName = _googleProfile.UserName ?? "";
        GoogleUserEmail = _googleProfile.UserEmail ?? "";
        GoogleStatus = _googleProfile.Status;
    }

    private void GoogleLogout()
    {
        _googleProfile.Logout();
        IsLoggedInGoogle = false;
        GoogleUserName = "";
        GoogleUserEmail = "";
        GoogleStatus = "Signed out";
        GoogleSyncStatus = "";
    }

    private async Task GoogleSyncUpAsync()
    {
        if (!IsLoggedInGoogle) { GoogleSyncStatus = "Sign in first"; return; }
        var active = _activeProfileTab?.Profile;
        if (active is null) { GoogleSyncStatus = "No active profile"; return; }

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(active);
            string fileName = $"fanzi_profile_{active.Name.Replace(' ', '_')}.json";
            bool ok = await _googleProfile.UploadProfileAsync(json, fileName, _disposeTokenSource.Token);
            GoogleSyncStatus = ok ? $"Uploaded '{active.Name}' to Google Drive" : "Upload failed";
        }
        catch (Exception ex) { GoogleSyncStatus = $"Sync up failed: {ex.Message}"; }
    }

    private async Task GoogleSyncDownAsync()
    {
        if (!IsLoggedInGoogle) { GoogleSyncStatus = "Sign in first"; return; }
        var active = _activeProfileTab?.Profile;
        if (active is null) { GoogleSyncStatus = "No active profile"; return; }

        try
        {
            string fileName = $"fanzi_profile_{active.Name.Replace(' ', '_')}.json";
            var json = await _googleProfile.DownloadProfileAsync(fileName, _disposeTokenSource.Token);
            if (json is not null)
            {
                var loaded = System.Text.Json.JsonSerializer.Deserialize<FanProfile>(json);
                if (loaded is not null && _activeProfileTab is not null)
                {
                    var existing = _activeProfileTab.Profile;
                    existing.Name = loaded.Name;
                    existing.CpuFanDesiredPercent = loaded.CpuFanDesiredPercent;
                    existing.CpuWarningThresholdDegrees = loaded.CpuWarningThresholdDegrees;
                    existing.NotificationEmail = loaded.NotificationEmail;
                    existing.FanCurve = loaded.FanCurve;
                    existing.FanChannelPercents = loaded.FanChannelPercents;
                    ApplyProfileToVm(existing);
                    GoogleSyncStatus = $"Downloaded '{existing.Name}' from Google Drive";
                }
            }
            else GoogleSyncStatus = "Profile not found in Google Drive";
        }
        catch (Exception ex) { GoogleSyncStatus = $"Sync down failed: {ex.Message}"; }
    }

    // ── AEDi Search & Navigate ────────────────────────────────────────────

    private void AediSearch()
    {
        if (string.IsNullOrWhiteSpace(AediQuery)) return;

        // Try quick command first
        var ping = _aediEngine.Ping(AediQuery);
        if (ping is not null)
        {
            AediResults = $"🎯 {ping.Entry.Title}\n{ping.Entry.Description}\n\n→ {ping.Entry.Action}";
            HasAediResults = true;
            AediNavigateTab = ping.TargetTab;
            SelectedTabIndex = ping.TargetTab;
            AediWelcome = $"Navigated to {ping.Entry.Section}";
            return;
        }

        // Full search
        var results = _aediEngine.Search(AediQuery);
        if (results.Count == 0)
        {
            AediResults = "No results found. Try: fans, rgb, clean, network, health, game, overlay, settings";
            HasAediResults = true;
            return;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine($"📌 {r.Entry.Title} ({r.Entry.Section})");
            sb.AppendLine($"   {r.Entry.Description}");
            sb.AppendLine($"   → {r.Entry.Action}");
            sb.AppendLine();
        }
        AediResults = sb.ToString().TrimEnd();
        HasAediResults = true;

        // Navigate to top result
        AediNavigateTab = results[0].TargetTab;
        SelectedTabIndex = results[0].TargetTab;
    }

    private void AediNavigate(object? tabIndex)
    {
        if (tabIndex is int i && i >= 0)
            SelectedTabIndex = i;
        else if (tabIndex is string s && int.TryParse(s, out int parsed) && parsed >= 0)
            SelectedTabIndex = parsed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeTokenSource.Cancel();

        // Only release the lock/token source when no refresh is in flight; a running refresh
        // still touches both and would throw ObjectDisposedException. (Neither holds unmanaged
        // resources here, so leaving them to the GC is safe.)
        if (_refreshLock.Wait(0))
        {
            _refreshLock.Dispose();
            _disposeTokenSource.Dispose();
        }

        DisposeOverlayStudio();
        try { _miniOverlay?.Close(); }
        catch (Exception ex) { AppLog.Warn("Closing the mini overlay failed", ex); }
        _miniOverlay = null;

        UnwireAlerts();
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
        try
        {
            if (_hardwareMonitorService.InitializationError is { } initError)
                StatusMessage = initError;

            Task<AppSettings> loadTask = _settingsService.LoadAsync(_disposeTokenSource.Token);
            Task delayTask = Task.Delay(TimeSpan.FromMilliseconds(800), _disposeTokenSource.Token);
            // The file is usually read long before the splash delay ends: honour "Reduce motion" for the loader too.
            if (await Task.WhenAny(loadTask, delayTask) == loadTask && loadTask.IsCompletedSuccessfully)
                ApplyEarlyMotionPreference(loadTask.Result);
            await delayTask;
            try
            {
                _appSettings = await loadTask;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Error("Loading settings failed — using defaults", ex);
                _appSettings = new AppSettings();
            }
            _appSettings.Migrate();
            _appSettings.Normalize();
            ApplySettingsToVm();
            RgbControl.ApplyOpenRgbSettings(_appSettings.OpenRgbPath, path =>
            {
                _appSettings.OpenRgbPath = path;
                _ = SaveSettingsAsync();
            });
            // v4 AEDi lighting switches + fan-header count for the RGB deep scan.
            RgbControl.FanHeaderCountProvider = () => FanChannels.Count;
            RgbControl.ApplyAiSettings(_appSettings.RgbSmartAmbient, _appSettings.RgbThermalGuardian, _appSettings.RgbNightDimming,
                (smart, guardian, night) =>
                {
                    _appSettings.RgbSmartAmbient = smart;
                    _appSettings.RgbThermalGuardian = guardian;
                    _appSettings.RgbNightDimming = night;
                    _ = SaveSettingsAsync();
                });
            InitialiseProfiles();
            EnsureStartupTaskMatchesSettings();

            try { SettingsLoaded?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { AppLog.Warn("SettingsLoaded handler failed", ex); }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed", ex);
            StatusMessage = $"Startup error: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }

        try
        {
            await RunRefreshLoopAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Refresh loop stopped", ex);
            if (!_disposed)
                StatusMessage = $"Hardware polling stopped: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Users upgrading from builds that wrote the (ignored) HKCU Run value get the scheduled task
    /// created on first launch, so "Start with Windows" actually works.
    /// </summary>
    private void EnsureStartupTaskMatchesSettings()
    {
        if (!_appSettings.StartWithWindows)
            return;

        _ = SafeExecuteAsync(async () =>
        {
            bool ok = await Task.Run(() => StartupService.IsRegistered || StartupService.SetStartup(true));
            if (!ok)
                StartupStatus = "Start with Windows could not be registered — see the log for details.";
        }, "Start with Windows");
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
            OverlayTransparent = _appSettings.OverlayTransparent;
            OverlayOpacity = _appSettings.OverlayOpacity;
            OverlayTopmost = _appSettings.OverlayTopmost;
            OverlayShowCpuHistory = _appSettings.OverlayShowCpuHistory;
            ToastNotificationsEnabled = _appSettings.ToastNotificationsEnabled;
            ReduceMotion = _appSettings.ReduceMotion;
            OverlayCompact = _appSettings.OverlayCompact;
            ApplyOverlayStudioSettings();
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
        _ = SaveSettingsAsync();
        StartupStatus = value ? "Registering startup task..." : "Removing startup task...";
        _ = SafeExecuteAsync(async () =>
        {
            bool ok = await Task.Run(() => StartupService.SetStartup(value));
            StartupStatus = (value, ok) switch
            {
                (true, true) => "FANZi will start (elevated, minimized) when you sign in.",
                (true, false) => "Could not create the startup task — see the log for details.",
                (false, true) => "Start with Windows disabled.",
                (false, false) => "Could not remove the startup task — see the log for details.",
            };
        }, "Start with Windows");
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
        OnPropertyChanged(nameof(FanCurveStatus));
        if (_suppressProfileSync) return;
        _appSettings.AiAutoFanEnabled = value;
        if (!value)
        {
            _aiEngine.Reset();
            _lastAiAppliedPercent = null;
            _lastAiAppliedChannelId = null;

            // The AI left the CPU fan at a manual duty — hand it back to the BIOS.
            if (CpuFanCanControl && _cpuFanChannelId is { Length: > 0 } channelId)
            {
                _ = SafeExecuteAsync(async () =>
                {
                    var result = await _hardwareMonitorService.RestoreAutomaticControlAsync(channelId, _disposeTokenSource.Token);
                    CpuFanCapability = result.Message;
                }, "Restore automatic CPU fan control");
            }
        }
        _ = SaveSettingsAsync();
    }

    partial void OnAiAnomalyDetectionChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.AiAnomalyDetection = value;
        _ = SaveSettingsAsync();
    }

    partial void OnOverlayTransparentChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.OverlayTransparent = value;
        _ = SaveSettingsAsync();
    }

    partial void OnOverlayOpacityChanged(double value)
    {
        if (_suppressProfileSync) return;
        _appSettings.OverlayOpacity = value;
        _ = SaveSettingsAsync();
    }

    partial void OnOverlayTopmostChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.OverlayTopmost = value;
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
        else
            LoadFanCurveFromProfile(tab.Profile);

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
        LoadFanCurveFromProfile(profile);
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
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception ex) { AppLog.Error("Saving settings failed", ex); }
    }

    // ── Refresh loop with adaptive polling ────────────────────────────────────

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            while (!_disposeTokenSource.IsCancellationRequested)
            {
                // v4 live sync: Tasks / Network / Power / RAM collect in the background, in parallel with the
                // hardware poll; finished results are applied in the same UI pass as the dashboard values. The
                // dashboard never waits for a slow panel — an unfinished collection lands on the next tick.
                if (_liveCollect is null)
                    _liveCollect = LiveSyncHub.Shared.CollectAsync(_disposeTokenSource.Token);
                var tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
                await RefreshAsync();

                int intervalMs = _isWindowVisible
                    ? _appSettings.PollingIntervalSeconds * 1000
                    : _appSettings.ReducedPollingIntervalSeconds * 1000;

                // Fixed cadence: subtract the time the poll itself took, so 1 s means one update per second.
                int elapsedMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;
                await Task.Delay(Math.Max(100, intervalMs - elapsedMs), _disposeTokenSource.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static readonly TimeSpan GameScanInterval = TimeSpan.FromSeconds(5);
    private long _lastGameScanTicks;
    private (bool IsGameRunning, string GameName) _lastGameScan = (false, "");

    /// <summary>Pending live-sync collection for the current cycle (null once committed).</summary>
    private Task<IReadOnlyList<Action>>? _liveCollect;

    /// <summary>
    /// Applies the panel collection if it has finished (UI thread, same pass as the dashboard values). Never
    /// waits: a collection still running stays pending and is applied on the next tick.
    /// </summary>
    private void CommitLiveSyncIfReady()
    {
        var pending = _liveCollect;
        if (pending is null || !pending.IsCompleted) return;
        _liveCollect = null;
        if (_disposed) return;

        IReadOnlyList<Action> applies = pending.Status == TaskStatus.RanToCompletion ? pending.Result : Array.Empty<Action>();
        if (pending.IsFaulted) AppLog.Warn("Live sync collection failed", pending.Exception);
        LiveSyncHub.Shared.Commit(applies);
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;

        // All bound properties/collections must be touched on the UI thread. Callers normally
        // are on it already (continuations resume on Avalonia's context); hop over if not.
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshAsync);
            return;
        }

        if (!await _refreshLock.WaitAsync(0)) return;

        try
        {
            IsRefreshing = true;
            var snapshot = await _hardwareMonitorService.GetSnapshotAsync(_disposeTokenSource.Token);

            // Same frame: finished panel results are committed right before the dashboard values below.
            CommitLiveSyncIfReady();

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
            CpuPackageTempValue = snapshot.CpuPackageTemperature;
            CpuAverageTempValue = snapshot.CpuAverageTemperature;
            CpuHotspotTempValue = snapshot.CpuHotspotTemperature;
            CpuGaugeTempValue = CpuTemperatureFallback.Select(
                snapshot.CpuPackageTemperature, snapshot.CpuHotspotTemperature, snapshot.CpuAverageTemperature);
            GpuCoreTempValue = snapshot.GpuCoreTemperature;
            GpuHotspotTempValue = snapshot.GpuHotspotTemperature;
            CpuLoadValue = snapshot.CpuTotalLoadPercent ?? 0;
            GpuLoadValue = snapshot.GpuLoadPercent ?? 0;
            CpuName = snapshot.CpuName ?? string.Empty;
            GpuName = snapshot.GpuName ?? string.Empty;
            GpuVram = FormatVram(snapshot.GpuMemoryUsedMb, snapshot.GpuMemoryTotalMb);
            // Friendly label: prefix with PUMP/AIO when CPU cooling is liquid-cooled
            if (snapshot.CpuFan is { } cf)
            {
                string kindPrefix = cf.DeviceKind switch
                {
                    FanDeviceKind.Pump        => "CPU Pump",
                    FanDeviceKind.AioCooler   => "CPU AIO Cooler",
                    FanDeviceKind.Watercooler => "CPU Watercooler",
                    FanDeviceKind.IoPump      => "CPU IO Pump",
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
            CpuFanRpmValue = snapshot.CpuFan?.SpeedRpm ?? 0;
            CpuFanControl = snapshot.CpuFan?.CurrentControlPercent is double control ? $"{control:F0}%" : "Auto/BIOS";
            CpuFanCapability = snapshot.CpuFan?.CapabilityMessage ?? "No dedicated CPU fan header was detected.";
            CpuFanCanControl = snapshot.CpuFan?.CanControl == true;
            _cpuFanChannelId = snapshot.CpuFan?.Id;

            // Don't fight the user: only follow the hardware value once the slider has been idle for 5 s.
            if (snapshot.CpuFan?.CurrentControlPercent is double cpuFanPercent
                && DateTime.UtcNow - _lastUserSliderChangeUtc >= SliderSyncHoldOff)
                SetCpuFanDesiredPercent(cpuFanPercent);

            FanCountLabel = snapshot.Fans.Count == 1 ? "1 channel" : $"{snapshot.Fans.Count} channels";
            LastUpdated = $"Updated {snapshot.Timestamp.LocalDateTime:HH:mm:ss}";
            StatusMessage = snapshot.StatusMessage;

            // Each consumer is isolated: one failing (e.g. a duplicate channel) must not stop the
            // others — in particular the RGB hardware-reactive feed — on every poll.
            RunPollStep("CPU readings", () => SyncCpuReadings(snapshot.CpuReadings));
            RunPollStep("GPU readings", () => SyncGpuReadings(snapshot.GpuReadings));
            RunPollStep("Sensor heatmap", () => UpdateHeatmap(snapshot.CpuReadings, snapshot.GpuReadings));
            RunPollStep("Fan channels", () => SynchronizeFans(snapshot.Fans));
            // Detect liquid cooling: CPU cooler reported as a Pump/AIO, or any pump in the fan list.
            var pump = snapshot.CpuFan is { DeviceKind: FanDeviceKind.Pump or FanDeviceKind.AioCooler }
                ? snapshot.CpuFan
                : snapshot.Fans.FirstOrDefault(f => f.DeviceKind is FanDeviceKind.Pump or FanDeviceKind.AioCooler);

            RunPollStep("RGB hardware feed", () =>
            {
                RgbControl.UpdateHardwareData(
                    cpuTempC: snapshot.CpuPackageTemperature ?? snapshot.CpuAverageTemperature,
                    gpuTempC: snapshot.GpuCoreTemperature,
                    cpuLoadPct: snapshot.CpuTotalLoadPercent,
                    pumpRpm: pump?.SpeedRpm,
                    isLiquidCooled: pump is not null);
            });
            RunPollStep("Thermal history", () => RecordHistory(
                snapshot.Timestamp.UtcDateTime,
                cpuTempC: CpuGaugeTempValue,
                gpuTempC: snapshot.GpuCoreTemperature,
                cpuLoadPct: snapshot.CpuTotalLoadPercent,
                gpuLoadPct: snapshot.GpuLoadPercent,
                coolerRpm: snapshot.CpuFan?.SpeedRpm ?? pump?.SpeedRpm));

            // ── AI Engine update ──────────────────────────────────────
            double? hottest = snapshot.CpuHotspotTemperature ?? snapshot.CpuPackageTemperature ?? snapshot.CpuAverageTemperature;
            double cpuLoad = snapshot.CpuTotalLoadPercent ?? 0;
            HottestTempValue = hottest;

            if (hottest.HasValue)
            {
                double suggested = _aiEngine.ComputeOptimalFanSpeed(
                    hottest.Value, cpuLoad, snapshot.CpuFan?.SpeedRpm,
                    _activeProfileTab?.Profile.FanCurve,
                    gpuTempC: snapshot.GpuCoreTemperature);

                AiSuggestedFan = $"{suggested:F0}%";
                AiTrend = _aiEngine.Predictor.Trend.ToString();
                AiPrediction = _aiEngine.Predictor.PredictedTempIn30s.HasValue
                    ? $"{_aiEngine.Predictor.PredictedTempIn30s.Value:F1}C in 30s"
                    : "Learning...";
                // Coach "30s FORECAST" slot: the bare value, "--" while learning (its status pill says so).
                AiForecast = _aiEngine.Predictor.PredictedTempIn30s is double forecast ? $"{forecast:F1}°C" : "--";
                AiStatus = _aiEngine.IsLearning ? "AEDi: Active" : "AEDi: Learning...";

                if (AiAutoFanEnabled && CpuFanCanControl && !string.IsNullOrEmpty(_cpuFanChannelId))
                {
                    // Only touch the hardware when the target actually moved (≥ 1 %) or the channel changed.
                    bool channelChanged = !string.Equals(_lastAiAppliedChannelId, _cpuFanChannelId, StringComparison.OrdinalIgnoreCase);
                    if (channelChanged || _lastAiAppliedPercent is not double last || Math.Abs(suggested - last) >= 1.0)
                    {
                        var aiResult = await _hardwareMonitorService.SetFanControlAsync(_cpuFanChannelId, suggested, _disposeTokenSource.Token);
                        if (aiResult.Success)
                        {
                            _lastAiAppliedPercent = suggested;
                            _lastAiAppliedChannelId = _cpuFanChannelId;
                        }
                    }
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
                        RaiseAnomalyAlert(report);
                    }
                    else
                    {
                        HasAnomaly = false;
                        AnomalyMessage = string.Empty;
                    }
                }
            }

            // ── IO-nity AI: Thermal Coach ─────────────────────────────
            // Pump-only / RPM-less systems are coached too: fall back to the pump RPM, else skip RPM rules.
            if (hottest.HasValue)
            {
                _thermalCoach.Record(hottest.Value, cpuLoad, snapshot.CpuFan?.SpeedRpm ?? pump?.SpeedRpm);
                var coachRecs = _thermalCoach.GetRecommendations();
                if (coachRecs.Count > 0)
                {
                    HasCoachRecommendations = true;
                    CoachRecommendations = string.Join("\n", coachRecs.Select(r =>
                        $"{(r.Severity == CoachSeverity.Critical ? "🔴" : r.Severity == CoachSeverity.Warning ? "🟡" : "🔵")} {r.Title}: {r.Message}"));
                }
                else
                {
                    HasCoachRecommendations = false;
                    CoachRecommendations = "";
                }
            }

            // ── IO-nity AI: Game Mode Detection ───────────────────────
            _gameModeDetector.Update(snapshot.GpuLoadPercent ?? 0, hottest ?? 0);
            // The process scan is the slowest part of a poll: with the 1 s real-time cadence it runs every 5 s.
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastGameScanTicks) >= GameScanInterval || _lastGameScanTicks == 0)
            {
                _lastGameScan = await _gameModeDetector.DetectAsync(_disposeTokenSource.Token);
                _lastGameScanTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            var gameDetection = _lastGameScan;
            if (gameDetection.IsGameRunning)
                _gameModeDetector.SetDetectedGame(gameDetection.GameName);
            IsGameMode = _gameModeDetector.IsGameModeActive;
            GameModeStatus = _gameModeDetector.StatusMessage;
            DetectedGame = _gameModeDetector.DetectedGame;
            GameModeDetail = _gameModeDetector.DetailMessage;
            RgbControl.UpdateGameState(IsGameMode, DetectedGame);

            // ── IO-nity AI: System Health Score ───────────────────────
            _healthScorer.Update(
                cpuTempC: snapshot.CpuPackageTemperature ?? snapshot.CpuAverageTemperature ?? 0,
                gpuTempC: snapshot.GpuCoreTemperature ?? 0,
                cpuLoadPct: snapshot.CpuTotalLoadPercent ?? 0,
                gpuLoadPct: snapshot.GpuLoadPercent ?? 0,
                fanRpm: snapshot.CpuFan?.SpeedRpm,
                fanPercent: snapshot.CpuFan?.CurrentControlPercent);
            HealthScore = $"{_healthScorer.CurrentScore:F0}";
            HealthScoreValue = _healthScorer.CurrentScore;
            HealthGrade = _healthScorer.Grade;
            HealthEmoji = _healthScorer.GradeEmoji;
            HealthSummary = _healthScorer.Summary;
            HealthInsights = string.Join(" • ", _healthScorer.Insights);
            _trayBaseTooltip = TrayTooltipFormatter.Format(
                snapshot.CpuPackageTemperature ?? snapshot.CpuAverageTemperature,
                snapshot.GpuCoreTemperature,
                _healthScorer.CurrentScore);
            RefreshTrayTooltip();

            // ── Temperature warning check ─────────────────────────────
            if (hottest.HasValue && hottest.Value >= CpuWarningThresholdDegrees)
            {
                HasCpuTempWarning = true;
                CpuTempWarningMessage = $"CPU temperature {hottest.Value:F0}C exceeds {CpuWarningThresholdDegrees:F0}C threshold.";
                RaiseTemperatureAlert(hottest.Value, CpuWarningThresholdDegrees);

                if (SmtpConfigured && DateTimeOffset.UtcNow - _lastAlertEmailSent > AlertEmailCooldown)
                {
                    _lastAlertEmailSent = DateTimeOffset.UtcNow;
                    double alertTemp = hottest.Value;
                    _ = SafeExecuteAsync(() => SendAlertEmailAsync(alertTemp), "Temperature alert email");
                }
            }
            else
            {
                HasCpuTempWarning = false;
                CpuTempWarningMessage = string.Empty;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (UnauthorizedAccessException ex)
        {
            LogPollError("Hardware poll", ex);
            StatusMessage = "Hardware poll failed: access denied. Run as Administrator.";
        }
        catch (Exception ex)
        {
            LogPollError("Hardware poll", ex);
            StatusMessage = $"Hardware poll error: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
            try { _refreshLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Runs one consumer of a poll; a failure is logged (rate-limited) and doesn't stop the rest.</summary>
    private void RunPollStep(string context, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            LogPollError(context, ex);
        }
    }

    private void LogPollError(string context, Exception ex)
    {
        string signature = $"{context}|{ex.GetType().FullName}|{ex.Message}";
        DateTime now = DateTime.UtcNow;
        if (signature == _lastPollErrorSignature && now - _lastPollErrorLoggedUtc < PollErrorLogCooldown)
            return;

        _lastPollErrorSignature = signature;
        _lastPollErrorLoggedUtc = now;
        AppLog.Error($"{context} failed", ex);
    }

    // ── Fan synchronization ───────────────────────────────────────────────────

    private void SynchronizeFans(IReadOnlyList<FanChannelSnapshot> fans)
    {
        // Tolerate duplicate IDs on either side (first wins) instead of throwing every poll.
        var existing = new Dictionary<string, FanChannelViewModel>(StringComparer.OrdinalIgnoreCase);
        var stale = new List<FanChannelViewModel>();
        foreach (var channel in FanChannels)
        {
            if (!existing.TryAdd(channel.Id, channel))
                stale.Add(channel);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in fans)
        {
            if (!seen.Add(snapshot.Id))
                continue;

            if (!existing.TryGetValue(snapshot.Id, out var vm))
            {
                vm = new FanChannelViewModel(
                    snapshot,
                    channel => SafeExecuteAsync(() => ApplyFanControlAsync(channel), $"Apply fan '{channel.Name}'"),
                    channel => SafeExecuteAsync(() => RestoreAutomaticControlAsync(channel), $"Auto mode for fan '{channel.Name}'"));
                if (_activeProfileTab?.Profile.FanChannelPercents.TryGetValue(snapshot.Id, out double stored) == true)
                    vm.DesiredPercent = stored;
                FanChannels.Add(vm);
                continue;
            }
            vm.Update(snapshot);
            existing.Remove(snapshot.Id);
        }

        stale.AddRange(existing.Values);
        foreach (var vm in stale)
        {
            vm.Dispose();
            FanChannels.Remove(vm);
        }
    }

    // Repeated names get their kind appended ("GPU Core · Load"); rows are replaced in place so an
    // unchanged list doesn't re-create every row container each poll.
    private void SyncCpuReadings(IReadOnlyList<CpuReadingSnapshot> readings)
    {
        var labels = SensorListLabels.Build(readings.Select(r => (r.Name, r.Kind)).ToList());
        SyncInPlace(CpuReadings, readings.Select((r, i) => r with { Name = labels[i] }).ToList());
    }

    private void SyncGpuReadings(IReadOnlyList<GpuReadingSnapshot> readings)
    {
        var labels = SensorListLabels.Build(readings.Select(r => (r.Name, r.Kind)).ToList());
        SyncInPlace(GpuReadings, readings.Select((r, i) => r with { Name = labels[i] }).ToList());
    }

    private static void SyncInPlace<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (i < target.Count)
            {
                if (!EqualityComparer<T>.Default.Equals(target[i], source[i])) target[i] = source[i];
            }
            else target.Add(source[i]);
        }
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
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

    /// <summary>
    /// Called on application exit before disposal: hands every controllable channel back to
    /// automatic/BIOS control so no fan is left pinned at a manual duty after FANZi closes.
    /// Bounded to a few seconds so a hung driver can't block exit.
    /// </summary>
    public void RestoreAutomaticControlOnExit()
    {
        var channelIds = FanChannels
            .Where(channel => channel.CanControl)
            .Select(channel => channel.Id)
            .ToList();
        if (CpuFanCanControl && !string.IsNullOrEmpty(_cpuFanChannelId))
            channelIds.Add(_cpuFanChannelId);

        channelIds = channelIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (channelIds.Count == 0)
            return;

        // The service runs each call on the thread pool, so waiting here can't deadlock the UI thread.
        var tasks = channelIds
            .Select(id => _hardwareMonitorService.RestoreAutomaticControlAsync(id, CancellationToken.None))
            .ToArray();
        try
        {
            if (!Task.WaitAll(tasks, TimeSpan.FromSeconds(3)))
                AppLog.Warn("Restoring automatic fan control on exit timed out.");
            else
                AppLog.Info($"Automatic fan control restored on {channelIds.Count} channel(s).");
        }
        catch (AggregateException ex)
        {
            AppLog.Warn("Restoring automatic fan control on exit partly failed", ex.Flatten());
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
            var result = await _hardwareMonitorService.SetFanControlAsync(_cpuFanChannelId, CpuFanDesiredPercent, _disposeTokenSource.Token);
            CpuFanCapability = result.Message;
            _lastAiAppliedPercent = null; // hardware no longer holds the AI's last value

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
            _lastAiAppliedPercent = null;
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
            // A user change (slider drag / binding) — hold off hardware sync for a few seconds.
            _lastUserSliderChangeUtc = DateTime.UtcNow;
            UpdateActiveProfileFromVm();
            _ = SaveSettingsAsync();
        }
    }

    partial void OnCpuWarningThresholdDegreesChanged(double value)
    {
        double clamped = Math.Clamp(value, 30, 110);
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

    private static string FormatTemperature(double? t) => t.HasValue ? $"{t.Value:F1}°C" : "--";
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
