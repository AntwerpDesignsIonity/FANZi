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

    // ── Commands ──────────────────────────────────────────────────────────────

    public IAsyncRelayCommand ConnectCommand       { get; }
    public IRelayCommand      DisconnectCommand    { get; }
    public IRelayCommand<RgbThemePreset> ApplyThemeCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public RgbControlViewModel(IRgbService rgbService)
    {
        _rgbService = rgbService;

        ConnectCommand    = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new RelayCommand(DisconnectFromServer);
        ApplyThemeCommand = new RelayCommand<RgbThemePreset>(ApplyTheme);

        // Sync slider sets from initial color constants.
        SyncPrimarySliders();
        SyncSecondarySliders();

        // Start the frame loop.
        _timer = new System.Timers.Timer(FrameIntervalMs);
        _timer.Elapsed += OnTimerTick;
        _timer.AutoReset = true;
        _timer.Start();
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
        if (++_frameCounter >= HardwareSendEveryNth)
        {
            _frameCounter = 0;
            if (_rgbService.IsConnected)
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

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _rgbService.Dispose();
    }
}
