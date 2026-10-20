using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.ViewModels;

/// <summary>A Typing FX style button, painted in its own colours.</summary>
public sealed record TypingFxChip(TypingFxStyle Style, string Label, string Glyph, IBrush Background, string Hint);

/// <summary>
/// 4.3 Typing FX: keystrokes become light. A background poller (<see cref="KeystrokeMonitor"/>) reports key codes,
/// <see cref="TypingFxEngine"/> turns them into per-LED frames for the keyboard (true key positions via OpenRGB's
/// LED names / matrix map) and — when "all devices" is on — for RAM, GPU, strips, mouse and monitor too, each
/// device receiving the animation remapped onto its own LEDs. Frames go out at the full 30 fps timer rate for
/// USB/HID devices; SMBus RAM is rate-limited inside <see cref="OpenRgbService"/>.
/// </summary>
public sealed partial class RgbControlViewModel
{
    private readonly TypingFxEngine _typingFx = new();
    private readonly KeystrokeMonitor _keystrokes = new();
    private Action<bool, string, bool>? _persistTypingFx;
    private bool _applyingTypingFxSettings;
    private double _lastKeyAt = -100;
    private int _keySeed;

    /// <summary>Per-device cache: keyboard LED name → LED position (for exact key placement).</summary>
    private Dictionary<string, LedPoint>? _keyboardLedPositions;
    private int _keyboardLedPositionsForDevice = -1;

    public IReadOnlyList<TypingFxChip> TypingFxChips { get; } =
    [
        new(TypingFxStyle.Ripple, "Ripple", "◎", Gradient("#06B6D4", "#8B5CF6"), TypingFxEngine.Hint(TypingFxStyle.Ripple)),
        new(TypingFxStyle.Pulse, "Pulse", "◐", Gradient("#7C3AED", "#EC4899"), TypingFxEngine.Hint(TypingFxStyle.Pulse)),
        new(TypingFxStyle.Line, "Line", "═", Gradient("#2563EB", "#22D3EE"), TypingFxEngine.Hint(TypingFxStyle.Line)),
        new(TypingFxStyle.Area, "Area", "▣", Gradient("#F59E0B", "#EF4444"), TypingFxEngine.Hint(TypingFxStyle.Area)),
        new(TypingFxStyle.Fireworks, "Fireworks", "✺", Gradient("#F43F5E", "#FBBF24"), TypingFxEngine.Hint(TypingFxStyle.Fireworks)),
        new(TypingFxStyle.Typewriter, "Typewriter", "▮", Gradient("#64748B", "#F8FAFC"), TypingFxEngine.Hint(TypingFxStyle.Typewriter)),
        new(TypingFxStyle.Matrix, "Matrix rain", "⋮", Gradient("#052E16", "#22C55E"), TypingFxEngine.Hint(TypingFxStyle.Matrix)),
        new(TypingFxStyle.Heat, "Typing heat", "♨", Gradient("#1D4ED8", "#F97316"), TypingFxEngine.Hint(TypingFxStyle.Heat)),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypingFxToggleText))]
    private bool _typingFxEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypingFxStyleLabel))]
    private TypingFxStyle _typingFxStyle = TypingFxStyle.Ripple;

    /// <summary>When on, every per-LED device joins in; off = keyboards (and keypads) only.</summary>
    [ObservableProperty] private bool _typingFxAllDevices = true;

    /// <summary>0.5 – 2.0: ring / beam / fade speed.</summary>
    [ObservableProperty] private double _typingFxSpeed = 1.0;

    /// <summary>0 – 1: how bright the running effect stays underneath while you type.</summary>
    [ObservableProperty] private double _typingFxBackground = 0.35;

    [ObservableProperty] private string _typingFxStatus = "Typing FX off — turn it on and start typing.";

    public string TypingFxToggleText => TypingFxEnabled ? "Typing FX ON" : "Typing FX off";
    public string TypingFxStyleLabel => TypingFxEngine.Label(TypingFxStyle);
    public bool TypingFxSupported => _keystrokes.IsSupported;

    private IRelayCommand<TypingFxStyle>? _selectTypingFxCommand;
    public IRelayCommand<TypingFxStyle> SelectTypingFxCommand =>
        _selectTypingFxCommand ??= new RelayCommand<TypingFxStyle>(style =>
        {
            TypingFxStyle = style;
            if (!TypingFxEnabled) TypingFxEnabled = true;
            TypingFxStatus = $"{TypingFxEngine.Label(style)}: {TypingFxEngine.Hint(style)}.";
        });

    private IRelayCommand? _typingFxDemoCommand;
    /// <summary>Fires a scripted burst of fake keystrokes so the effect can be seen without typing.</summary>
    public IRelayCommand TypingFxDemoCommand => _typingFxDemoCommand ??= new RelayCommand(() => _ = TypingFxDemoAsync());

    /// <summary>Loads persisted switches and the save callback (called by MainWindowViewModel).</summary>
    public void ApplyTypingFxSettings(bool enabled, string? style, bool allDevices, Action<bool, string, bool> persist)
    {
        _applyingTypingFxSettings = true;
        try
        {
            TypingFxStyle = TypingFxEngine.ParseStyle(style);
            TypingFxAllDevices = allDevices;
            TypingFxEnabled = enabled;
        }
        finally { _applyingTypingFxSettings = false; }
        _persistTypingFx = persist;
    }

    partial void OnTypingFxEnabledChanged(bool value)
    {
        if (value)
        {
            if (!_keystrokes.IsSupported)
            {
                TypingFxStatus = "Typing FX needs Windows (system-wide key events) — not available on this OS.";
                TypingFxEnabled = false;
                return;
            }
            _keystrokes.KeyDown -= OnKeyDown;
            _keystrokes.KeyDown += OnKeyDown;
            _keystrokes.Start();
            TypingFxStatus = $"{TypingFxEngine.Label(TypingFxStyle)} armed — type anywhere. Only key codes are read, nothing is logged.";
            AppLog.Info($"RGB: Typing FX on ({TypingFxStyle})");
        }
        else
        {
            _keystrokes.KeyDown -= OnKeyDown;
            _keystrokes.Stop();
            if (!_applyingTypingFxSettings) TypingFxStatus = "Typing FX off.";
        }
        PersistTypingFx();
    }

    partial void OnTypingFxStyleChanged(TypingFxStyle value)
    {
        _typingFx.Style = value;
        PersistTypingFx();
    }

    partial void OnTypingFxAllDevicesChanged(bool value) => PersistTypingFx();
    partial void OnTypingFxSpeedChanged(double value) => _typingFx.Speed = Math.Clamp(value, 0.25, 3.0);
    partial void OnTypingFxBackgroundChanged(double value) => _typingFx.BackgroundLevel = Math.Clamp(value, 0, 1);

    private void PersistTypingFx()
    {
        if (!_applyingTypingFxSettings) _persistTypingFx?.Invoke(TypingFxEnabled, TypingFxStyle.ToString(), TypingFxAllDevices);
    }

    private void OnKeyDown(int vk)
    {
        double now = _stopwatch.Elapsed.TotalSeconds;
        var origin = ExactKeyPosition(vk) ?? KeyboardGeometry.Position(vk);
        _typingFx.Press(now, origin, KeyboardGeometry.IsBreakKey(vk), unchecked(++_keySeed * 31 + vk));
        _lastKeyAt = now;
    }

    /// <summary>Exact LED position from the connected keyboard's LED names, when OpenRGB labels them.</summary>
    private LedPoint? ExactKeyPosition(int vk)
    {
        var kb = Devices.FirstOrDefault(d => d.Category is RgbDeviceCategory.Keyboard && d.Layout is not null && d.LedNames.Count > 0);
        if (kb is null) return null;
        if (_keyboardLedPositionsForDevice != kb.DeviceIndex || _keyboardLedPositions is null)
        {
            var map = new Dictionary<string, LedPoint>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < kb.LedNames.Count && i < kb.Layout!.Points.Count; i++)
                if (!string.IsNullOrWhiteSpace(kb.LedNames[i])) map[kb.LedNames[i].Trim()] = kb.Layout.Points[i];
            _keyboardLedPositions = map;
            _keyboardLedPositionsForDevice = kb.DeviceIndex;
        }
        string? name = KeyboardGeometry.OpenRgbLedName(vk);
        return name is not null && _keyboardLedPositions.TryGetValue(name, out var p) ? p : null;
    }

    /// <summary>True while a keystroke effect is still visible (hits alive or the typing-rate glow is up).</summary>
    private bool TypingFxActive(double now) =>
        TypingFxEnabled && (now - _lastKeyAt < TypingFxEngine.HitLifetime / Math.Max(0.25, _typingFx.Speed) + 1.5 || _typingFx.KeysPerSecond > 0.2);

    /// <summary>
    /// One Typing FX frame for every participating device. Called from the timer at the full frame rate while
    /// active; the base colour is the running effect so the lights blend back to normal when you stop.
    /// </summary>
    private async Task SendTypingFxFrameAsync(double now, RgbColor baseColor)
    {
        try
        {
            _typingFx.Tick(now);
            var devices = Devices.ToArray();
            // Fade the background back to full once typing stops (1.5 s after the last key, over 1.5 s).
            double idle = Math.Clamp((now - _lastKeyAt - 1.5) / 1.5, 0, 1);
            double savedLevel = _typingFx.BackgroundLevel;
            _typingFx.BackgroundLevel = savedLevel + (1 - savedLevel) * idle;

            var frame = FrameParameters();
            foreach (var d in devices)
            {
                if (!d.IsControllable || d.LedCount <= 0 || d.Layout is null) continue;
                bool isKeyboard = d.Category is RgbDeviceCategory.Keyboard or RgbDeviceCategory.Keypad;
                if (!isKeyboard && !TypingFxAllDevices) continue;

                var colors = _typingFx.Render(now, d.Layout, baseColor, frame.Primary, frame.Secondary, isKeyboard);
                await _rgbService.SetDeviceColorsAsync(d.DeviceIndex, colors, _cts.Token);
            }
            _typingFx.BackgroundLevel = savedLevel;

            if ((_frameCounter & 7) == 0)
            {
                double wpm = _typingFx.WordsPerMinute;
                string status = $"{TypingFxEngine.Label(TypingFxStyle)} · {_keystrokes.KeyCount} keys · ~{wpm:F0} WPM · {_typingFx.ActiveHits} live";
                Avalonia.Threading.Dispatcher.UIThread.Post(() => TypingFxStatus = status);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogFrameFailure(ex); }
    }

    private async Task TypingFxDemoAsync()
    {
        if (!TypingFxEnabled) TypingFxEnabled = true;
        string demo = "FANZi IO-nity Typing FX ";
        var rnd = new Random(7);
        foreach (char ch in demo)
        {
            int vk = char.IsLetter(ch) ? char.ToUpperInvariant(ch) : ch == ' ' ? 0x20 : ch == '-' ? 0xBD : '0' + rnd.Next(10);
            OnKeyDown(vk);
            try { await Task.Delay(90 + rnd.Next(70), _cts.Token); } catch (OperationCanceledException) { return; }
        }
        OnKeyDown(0x0D);
    }

    private void DisposeTypingFx()
    {
        _keystrokes.KeyDown -= OnKeyDown;
        _keystrokes.Dispose();
    }
}
