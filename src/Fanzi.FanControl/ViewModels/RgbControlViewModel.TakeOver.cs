using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// 4.6 "it just works" RGB: with <see cref="AutoTakeOver"/> on, FANZi closes lighting-only apps that repaint the same
/// hardware (see <see cref="RgbTakeOver"/>) as soon as it sees them — no banner, no button — then rescans and lights
/// the devices. Suites that also run cooling or peripherals are never closed automatically.
/// </summary>
public partial class RgbControlViewModel
{
    private readonly RgbTakeOverThrottle _takeOverThrottle = new();
    private Action<bool>? _persistTakeOver;
    private bool _applyingTakeOver;
    private bool _takingOver;

    /// <summary>Close other lighting apps automatically (persisted as AppSettings.RgbAutoTakeOver).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoTakeOverText))]
    private bool _autoTakeOver;

    /// <summary>What the last automatic take-over did ("" until it ran).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoTakeOverText))]
    private string _takeOverStatus = "";

    public string AutoTakeOverText => !AutoTakeOver
        ? "Off: another lighting app keeps the lights until you close it."
        : TakeOverStatus.Length > 0
            ? TakeOverStatus
            : "On: FANZi closes other lighting-only apps (Aura, Mystic Light, RGB Fusion, Polychrome, SignalRGB, RAM tools) and lights everything itself.";

    /// <summary>Loads the persisted switch and its save callback (called by MainWindowViewModel).</summary>
    public void ApplyTakeOverSettings(bool autoTakeOver, Action<bool> persist)
    {
        _applyingTakeOver = true;
        try { AutoTakeOver = autoTakeOver; }
        finally { _applyingTakeOver = false; }
        _persistTakeOver = persist;
    }

    partial void OnAutoTakeOverChanged(bool value)
    {
        if (_applyingTakeOver) return;
        _persistTakeOver?.Invoke(value);
        _takeOverThrottle.Reset();
        TakeOverStatus = "";
        AppLog.Info($"RGB: automatic take-over {(value ? "on" : "off")}.");
        if (value) RefreshHardwareAdvisories();
    }

    /// <summary>
    /// Called with every conflict check. Starts one automatic close when the switch is on, RGB is on, the endpoint
    /// is this PC and the throttle allows it. Never throws.
    /// </summary>
    internal void MaybeAutoTakeOver(IReadOnlyList<RgbConflictDetector.Conflict> conflicts)
    {
        if (!AutoTakeOver || !RgbEnabled || !EndpointIsLocal || _takingOver || conflicts.Count == 0) return;

        var now = RgbTakeOver.AutoClosable(conflicts);
        var targets = _takeOverThrottle.Acquire(DateTime.UtcNow, now);
        if (targets.Count == 0)
        {
            var stuck = now.Where(c => _takeOverThrottle.GaveUpOn(c.Product)).Select(c => c.Product).ToArray();
            if (stuck.Length > 0)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => TakeOverStatus =
                    $"{string.Join(", ", stuck)} keeps coming back after {RgbTakeOverThrottle.MaxAttempts} closes — use \"Stop it — FANZi takes over\" below to stop it for good.");
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = AutoTakeOverAsync(targets));
    }

    private async Task AutoTakeOverAsync(IReadOnlyList<RgbConflictDetector.Conflict> targets)
    {
        if (_takingOver) return;
        _takingOver = true;
        string names = RgbConflictDetector.Describe(targets);
        try
        {
            AppLog.Info($"RGB: automatic take-over — closing {names} for this session.");
            ConnectionStatus = $"Taking over the lights from {names}…";
            int stopped = await Task.Run(() => RgbConflictDetector.Stop(targets, disableAutostart: false));

            // Same follow-up as the manual button: decide the SMBus owner again at once, then let OpenRGB detect again.
            _releaseStreak = 99;
            EvaluateCoexistence(RgbConflictDetector.Find(), out _);
            await RestartServerForCompatibilityAsync(force: true);
            TakeOverStatus = stopped > 0
                ? $"On: closed {names} for this session — FANZi is lighting everything."
                : $"On: {names} could not be closed (is FANZi running as Administrator?).";
        }
        catch (Exception ex)
        {
            AppLog.Warn("RGB: automatic take-over failed", ex);
            TakeOverStatus = $"On: taking over from {names} failed — {ex.Message}";
        }
        finally
        {
            _takingOver = false;
        }
    }
}
