using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// ViewModel for the RGB lighting control panel.
/// Drives the <see cref="RgbEffectsEngine"/> on a 30 fps timer loop,
/// forwards colours to <see cref="IRgbService"/>, and exposes all
/// user-configurable settings as observable properties.
/// </summary>
public sealed partial class RgbControlViewModel : ViewModelBase, IDisposable
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const int   FrameIntervalMs       = 33;   // ~30 fps
    private const int   HardwareSendEveryNth  = 3;    // send to OpenRGB every ~100 ms

    // ── Dependencies ──────────────────────────────────────────────────────────
    private readonly IRgbService             _rgbService;
    private readonly System.Timers.Timer     _timer;
    private readonly Stopwatch               _stopwatch = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts       = new();

    // ── Hardware data (set by MainWindowViewModel on each refresh) ────────────
    private double? _cpuTempC;
    private double? _gpuTempC;
    private double? _cpuLoadPct;

    // Frame counter for throttling hardware sends.
    private int _frameCounter;

    // Current device list (populated after connect).
    private int _deviceCount = 1;

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty]
    private string _connectionStatus = "Not connected — start OpenRGB with SDK server enabled";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(ConnectionBadgeBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusLabel))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusLabelColor))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    private bool _isConnecting;

    // Computed UI properties – avoids BoolConverters.IsTrue in compiled bindings
    public string ConnectionBadgeBackground  => IsConnected ? "#0D291A" : "#1C0C0C";
    public string ConnectionBadgeBorderBrush => IsConnected ? "#22C55E" : "#EF4444";
    public string ConnectionStatusLabel      => IsConnected ? "● LIVE"  : "● OFFLINE";
    public string ConnectionStatusLabelColor => IsConnected ? "#22C55E" : "#EF4444";
    public string ConnectButtonText          => IsConnecting ? "Connecting…" : "Connect";

    [ObservableProperty]
    private string _openRgbHost = "localhost";

    [ObservableProperty]
    private int _openRgbPort = 6742;

    // ── Effect / theme settings ───────────────────────────────────────────────

    [ObservableProperty]
    private RgbEffectType _selectedEffect = RgbEffectType.Pulse;

    [ObservableProperty]
    private RgbColor _primaryColor = RgbColor.Blue;

    [ObservableProperty]
    private RgbColor _secondaryColor = RgbColor.Cyan;

    [ObservableProperty]
    private double _speedMultiplier = 1.0;

    [ObservableProperty]
    private double _brightness = 1.0;

    [ObservableProperty]
    private bool _hardwareReactiveEnabled = true;

    // ── Per-channel colour editors (R/G/B sliders + hex) ─────────────────────

    [ObservableProperty]
    private int _primaryR = 0;

    [ObservableProperty]
    private int _primaryG = 100;

    [ObservableProperty]
    private int _primaryB = 255;

    [ObservableProperty]
    private string _primaryHex = "#0064FF";

    [ObservableProperty]
    private int _secondaryR = 0;

    [ObservableProperty]
    private int _secondaryG = 200;

    [ObservableProperty]
    private int _secondaryB = 255;

    [ObservableProperty]
    private string _secondaryHex = "#00C8FF";

    // ── Live preview ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private RgbColor _previewColor = RgbColor.Blue;

    [ObservableProperty]
    private string _previewHex = "#0064FF";

    // ── Device list ───────────────────────────────────────────────────────────

    public ObservableCollection<RgbDeviceInfo> Devices { get; } = new();

    [ObservableProperty]
    private string _deviceSummary = "No devices detected";

    // ── Effect display ────────────────────────────────────────────────────────

    public ObservableCollection<RgbEffectType> AvailableEffects { get; } =
    [
        RgbEffectType.Static,
        RgbEffectType.Pulse,
        RgbEffectType.Rainbow,
        RgbEffectType.ColorWave,
        RgbEffectType.TemperatureReactive,
        RgbEffectType.CpuLoadReactive,
        RgbEffectType.Performance,
        RgbEffectType.Strobe,
        RgbEffectType.DualColorFlash,
    ];

    // ── Theme presets ─────────────────────────────────────────────────────────

    public IReadOnlyList<RgbThemePreset> Themes => RgbThemePreset.All;

    [ObservableProperty]
    private RgbThemePreset? _activeTheme;

    // ── Server management ────────────────────────────────────────────────────

    private readonly OpenRgbServerManager _serverManager = new();

    [ObservableProperty]
    private string _serverStatus = "Detecting OpenRGB...";

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private bool _isStartingServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallButton))]
    private bool _isInstallingOpenRgb;

    [ObservableProperty]
    private string _installProgress = "";

    public bool IsOpenRgbInstalled => _serverManager.IsInstalled;
    public bool ShowInstallButton => !IsOpenRgbInstalled && !IsInstallingOpenRgb;

    public string StartServerButtonText => IsStartingServer ? "Starting..." : IsServerRunning ? "Server Running" : "Start Server";

    /// <summary>Master enable/disable for RGB. When off, the server is stopped and the engine pauses.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RgbToggleText))]
    private bool _rgbEnabled = true;

    public string RgbToggleText => RgbEnabled ? "RGB Enabled" : "RGB Disabled";

    // ��─ Commands ─────────────────��────────────────────────────────────────────

    public IAsyncRelayCommand ConnectCommand       { get; }
    public IRelayCommand      DisconnectCommand    { get; }
    public IRelayCommand<RgbThemePreset> ApplyThemeCommand { get; }
    public IAsyncRelayCommand StartServerCommand   { get; }
    public IRelayCommand      StopServerCommand    { get; }
    public IAsyncRelayCommand InstallOpenRgbCommand { get; }
    public IAsyncRelayCommand RescanDevicesCommand { get; }
    public IAsyncRelayCommand RestartServerCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public RgbControlViewModel(IRgbService rgbService)
    {
        _rgbService = rgbService;

        ConnectCommand    = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new RelayCommand(DisconnectFromServer);
        ApplyThemeCommand = new RelayCommand<RgbThemePreset>(ApplyTheme);
        StartServerCommand = new AsyncRelayCommand(StartServerAsync);
        StopServerCommand  = new RelayCommand(StopServer);
        InstallOpenRgbCommand = new AsyncRelayCommand(InstallOpenRgbAsync);
        RescanDevicesCommand = new AsyncRelayCommand(RescanDevicesAsync);
        RestartServerCommand = new AsyncRelayCommand(RestartServerAsync);

        _serverManager.DetectInstallation();
        ServerStatus = _serverManager.Status;
        OnPropertyChanged(nameof(IsOpenRgbInstalled));
        OnPropertyChanged(nameof(ShowInstallButton));

        // Sync slider sets from initial color constants.
        SyncPrimarySliders();
        SyncSecondarySliders();

        // Start the frame loop.
        _timer = new System.Timers.Timer(FrameIntervalMs);
        _timer.Elapsed += OnTimerTick;
        _timer.AutoReset = true;
        _timer.Start();

        // Auto-start OpenRGB server in the background and auto-connect.
        // No user action needed — server boots silently with FANZI.
        _ = AutoStartOpenRgbAsync();
    }

    private async Task AutoStartOpenRgbAsync()
    {
        try
        {
            var progress = new Progress<string>(msg =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ServerStatus = msg));

            // EnsureRunningAsync: detect → install if missing → start server (waits for port)
            bool ok = await _serverManager.EnsureRunningAsync(OpenRgbPort, progress, _cts.Token);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsServerRunning = ok;
                ServerStatus = _serverManager.Status;
                OnPropertyChanged(nameof(IsOpenRgbInstalled));
                OnPropertyChanged(nameof(ShowInstallButton));
                OnPropertyChanged(nameof(StartServerButtonText));
            });

            // Connect with retry — OpenRGB may need a few extra seconds to scan devices
            // even after the SDK port is listening.
            for (int attempt = 1; attempt <= 8 && !IsConnected && !_cts.Token.IsCancellationRequested; attempt++)
            {
                if (!OpenRgbServerManager.IsPortListening(OpenRgbPort))
                {
                    // Server not actually ready — wait and try again
                    await Task.Delay(1500, _cts.Token);
                    continue;
                }

                await ConnectAsync();
                if (IsConnected) break;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    ServerStatus = $"Waiting for OpenRGB to be ready... (attempt {attempt}/8)");
                await Task.Delay(2000, _cts.Token);
            }

            // Start a background watchdog that auto-reconnects if the link drops
            _ = ConnectionWatchdogAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ServerStatus = $"Auto-start failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Background loop that re-establishes the OpenRGB connection if it ever drops.
    /// Runs every 5 seconds while RGB is enabled.
    /// </summary>
    private async Task ConnectionWatchdogAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, _cts.Token);
                if (!RgbEnabled) continue;

                if (!_rgbService.IsConnected && OpenRgbServerManager.IsPortListening(OpenRgbPort))
                {
                    // Server's there but our client dropped — reconnect silently
                    await ConnectAsync();
                }
                else if (!OpenRgbServerManager.IsPortListening(OpenRgbPort))
                {
                    // Port died — server might have crashed; restart it
                    await _serverManager.EnsureRunningAsync(OpenRgbPort, null, _cts.Token);
                    if (OpenRgbServerManager.IsPortListening(OpenRgbPort))
                        await ConnectAsync();
                }
                else if (_rgbService.IsConnected && Devices.Count == 0)
                {
                    // Connected but no devices yet — OpenRGB may still be detecting hardware
                    await RefreshDevicesAsync();
                }
            }
            catch (OperationCanceledException) { return; }
            catch { /* keep watchdog alive */ }
        }
    }

    /// <summary>Toggle handler: when user flips RGB on/off, start or stop the engine + server.</summary>
    partial void OnRgbEnabledChanged(bool value)
    {
        if (value)
        {
            _ = AutoStartOpenRgbAsync();
        }
        else
        {
            // Disable RGB: stop sending frames, disconnect, stop server
            DisconnectFromServer();
            _serverManager.StopServer();
            IsServerRunning = false;
            ServerStatus = "RGB disabled";
            OnPropertyChanged(nameof(StartServerButtonText));
        }
    }

    // ── Hardware data update (called by MainWindowViewModel) ──────────────────

    /// <summary>
    /// Called by <see cref="MainWindowViewModel"/> each time new hardware
    /// data is available.  Thread-safe (volatile fields).
    /// </summary>
    public void UpdateHardwareData(double? cpuTempC, double? gpuTempC, double? cpuLoadPct)
    {
        _cpuTempC   = cpuTempC;
        _gpuTempC   = gpuTempC;
        _cpuLoadPct = cpuLoadPct;
    }

    // ── Timer tick (frame loop) ───────────────────────────────────────────────

    private void OnTimerTick(object? sender, ElapsedEventArgs e)
    {
        double elapsed = _stopwatch.Elapsed.TotalSeconds;

        // Use hardware-reactive data only when the toggle is on.
        double? cpuTemp = HardwareReactiveEnabled ? _cpuTempC : null;
        double? gpuTemp = HardwareReactiveEnabled ? _gpuTempC : null;
        double? cpuLoad = HardwareReactiveEnabled ? _cpuLoadPct : null;

        RgbColor color = RgbEffectsEngine.Tick(
            elapsedSeconds:  elapsed,
            effect:          SelectedEffect,
            primary:         PrimaryColor,
            secondary:       SecondaryColor,
            speedMultiplier: SpeedMultiplier,
            brightness:      Brightness,
            cpuTempC:        cpuTemp,
            gpuTempC:        gpuTemp,
            cpuLoadPct:      cpuLoad,
            deviceIndex:     0,
            deviceCount:     Math.Max(1, _deviceCount));

        // Update UI preview (Avalonia requires Dispatcher for property changes).
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PreviewColor = color;
            PreviewHex   = color.ToHex();
        });

        // Send to hardware every Nth frame to avoid overwhelming OpenRGB.
        // Gated on RgbEnabled — when user toggles off, frames stop reaching hardware.
        if (++_frameCounter >= HardwareSendEveryNth)
        {
            _frameCounter = 0;
            if (RgbEnabled && _rgbService.IsConnected)
            {
                // Fire-and-forget; errors are swallowed inside the service.
                _ = SendFrameToHardwareAsync(elapsed, color);
            }
        }
    }

    private async Task SendFrameToHardwareAsync(double elapsed, RgbColor masterColor)
    {
        try
        {
            if (_deviceCount <= 1)
            {
                await _rgbService.SetAllDevicesColorAsync(masterColor, _cts.Token);
            }
            else
            {
                // Each device gets a phase-shifted colour for wave effects.
                for (int i = 0; i < _deviceCount; i++)
                {
                    RgbColor deviceColor = RgbEffectsEngine.Tick(
                        elapsedSeconds:  elapsed,
                        effect:          SelectedEffect,
                        primary:         PrimaryColor,
                        secondary:       SecondaryColor,
                        speedMultiplier: SpeedMultiplier,
                        brightness:      Brightness,
                        cpuTempC:        HardwareReactiveEnabled ? _cpuTempC : null,
                        gpuTempC:        HardwareReactiveEnabled ? _gpuTempC : null,
                        cpuLoadPct:      HardwareReactiveEnabled ? _cpuLoadPct : null,
                        deviceIndex:     i,
                        deviceCount:     _deviceCount);

                    await _rgbService.SetDeviceColorAsync(i, deviceColor, _cts.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* swallow — connection may have dropped */ }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private async Task ConnectAsync()
    {
        if (IsConnecting) return;
        IsConnecting     = true;
        ConnectionStatus = $"Connecting to {OpenRgbHost}:{OpenRgbPort}…";

        bool ok = await _rgbService.TryConnectAsync(
            OpenRgbHost, OpenRgbPort, _cts.Token);

        if (ok)
        {
            IsConnected      = true;
            ConnectionStatus = $"Connected — {_rgbService.ServerVersion}";
            await RefreshDevicesAsync();
        }
        else
        {
            IsConnected      = false;
            ConnectionStatus = _rgbService.ServerVersion; // contains the error message
        }

        IsConnecting = false;
    }

    private void DisconnectFromServer()
    {
        _rgbService.Disconnect();
        IsConnected      = false;
        ConnectionStatus = "Disconnected";
        Devices.Clear();
        _deviceCount     = 1;
        DeviceSummary    = "No devices detected";
    }

    private async Task RefreshDevicesAsync()
    {
        var list = await _rgbService.GetDevicesAsync(_cts.Token);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Devices.Clear();
            foreach (var d in list) Devices.Add(d);
            _deviceCount  = Math.Max(1, Devices.Count);
            DeviceSummary = Devices.Count == 0
                ? "No RGB devices found in OpenRGB"
                : $"{Devices.Count} device{(Devices.Count == 1 ? "" : "s")} connected";
        });
    }

    private void ApplyTheme(RgbThemePreset? preset)
    {
        if (preset is null) return;
        ActiveTheme      = preset;
        SelectedEffect   = preset.Effect;
        SpeedMultiplier  = preset.SpeedMultiplier;
        Brightness       = preset.Brightness;
        PrimaryColor     = preset.PrimaryColor;
        SecondaryColor   = preset.SecondaryColor;
        SyncPrimarySliders();
        SyncSecondarySliders();
    }

    // ── Slider ↔ colour sync ──────────────────────────────────────────────────

    private bool _updatingPrimary;
    private bool _updatingSecondary;

    /// <summary>Called when any primary RGB slider changes.</summary>
    public void OnPrimarySliderChanged()
    {
        if (_updatingPrimary) return;
        _updatingPrimary = true;
        PrimaryColor     = new RgbColor((byte)PrimaryR, (byte)PrimaryG, (byte)PrimaryB);
        PrimaryHex       = PrimaryColor.ToHex();
        _updatingPrimary = false;
    }

    /// <summary>Called when the primary hex text box loses focus.</summary>
    public void HandlePrimaryHexInput(string hex)
    {
        if (_updatingPrimary) return;
        if (RgbColor.TryParseHex(hex, out var c))
        {
            _updatingPrimary = true;
            PrimaryColor     = c;
            PrimaryR         = c.R;
            PrimaryG         = c.G;
            PrimaryB         = c.B;
            _updatingPrimary = false;
        }
    }

    /// <summary>Called when any secondary RGB slider changes.</summary>
    public void OnSecondarySliderChanged()
    {
        if (_updatingSecondary) return;
        _updatingSecondary = true;
        SecondaryColor     = new RgbColor((byte)SecondaryR, (byte)SecondaryG, (byte)SecondaryB);
        SecondaryHex       = SecondaryColor.ToHex();
        _updatingSecondary = false;
    }

    /// <summary>Called when the secondary hex text box loses focus.</summary>
    public void HandleSecondaryHexInput(string hex)
    {
        if (_updatingSecondary) return;
        if (RgbColor.TryParseHex(hex, out var c))
        {
            _updatingSecondary = true;
            SecondaryColor     = c;
            SecondaryR         = c.R;
            SecondaryG         = c.G;
            SecondaryB         = c.B;
            _updatingSecondary = false;
        }
    }

    private void SyncPrimarySliders()
    {
        _updatingPrimary = true;
        PrimaryR         = PrimaryColor.R;
        PrimaryG         = PrimaryColor.G;
        PrimaryB         = PrimaryColor.B;
        PrimaryHex       = PrimaryColor.ToHex();
        _updatingPrimary = false;
    }

    private void SyncSecondarySliders()
    {
        _updatingSecondary = true;
        SecondaryR         = SecondaryColor.R;
        SecondaryG         = SecondaryColor.G;
        SecondaryB         = SecondaryColor.B;
        SecondaryHex       = SecondaryColor.ToHex();
        _updatingSecondary = false;
    }

    // ── Server management commands ────────────────────────────────────────────

    private async Task StartServerAsync()
    {
        if (IsStartingServer || IsServerRunning) return;
        IsStartingServer = true;
        OnPropertyChanged(nameof(StartServerButtonText));

        bool ok = await _serverManager.StartServerAsync(OpenRgbPort, _cts.Token);
        IsServerRunning = ok;
        ServerStatus = _serverManager.Status;
        IsStartingServer = false;
        OnPropertyChanged(nameof(StartServerButtonText));

        if (ok && !IsConnected)
        {
            await Task.Delay(500, _cts.Token);
            await ConnectAsync();
        }
    }

    private void StopServer()
    {
        _serverManager.StopServer();
        IsServerRunning = false;
        ServerStatus = _serverManager.Status;
        OnPropertyChanged(nameof(StartServerButtonText));
    }

    private async Task RescanDevicesAsync()
    {
        if (!_rgbService.IsConnected)
        {
            ServerStatus = "Not connected — flip RGB toggle ON first";
            return;
        }
        ServerStatus = "Rescanning OpenRGB devices...";
        await RefreshDevicesAsync();
        ServerStatus = $"Rescan complete — {Devices.Count} device(s) detected";
    }

    private async Task RestartServerAsync()
    {
        ServerStatus = "Restarting OpenRGB server...";
        DisconnectFromServer();
        _serverManager.StopServer();
        await Task.Delay(800, _cts.Token);
        await AutoStartOpenRgbAsync();
    }

    private async Task InstallOpenRgbAsync()
    {
        if (IsInstallingOpenRgb) return;
        IsInstallingOpenRgb = true;
        InstallProgress = "Starting download...";
        ServerStatus = "Installing OpenRGB...";

        var progress = new Progress<string>(msg =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => InstallProgress = msg);
        });

        bool ok = await _serverManager.InstallOpenRgbAsync(progress, _cts.Token);

        IsInstallingOpenRgb = false;
        ServerStatus = _serverManager.Status;
        OnPropertyChanged(nameof(IsOpenRgbInstalled));
        OnPropertyChanged(nameof(ShowInstallButton));
        InstallProgress = ok ? "Installed! Ready to start." : "Install failed.";

        if (ok)
        {
            await Task.Delay(500, _cts.Token);
            await StartServerAsync();
        }
    }

    // ── IDisposable ─────────���────────────────────────���────────────────────────

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _serverManager.Dispose();
        _rgbService.Dispose();
    }
}
