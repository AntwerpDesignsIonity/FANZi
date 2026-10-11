using Fanzi.FanControl.Services.Rgb;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// v3 RGB hardware coverage: conflicting RGB software, the PawnIO driver (RAM / motherboard RGB),
/// and an automatic restart of an OpenRGB server that refuses FANZi's mode changes.
/// </summary>
public partial class RgbControlViewModel
{
    // ── Conflicting RGB software ─────────────────────────────────────────────

    private IReadOnlyList<RgbConflictDetector.Conflict> _conflicts = Array.Empty<RgbConflictDetector.Conflict>();

    [ObservableProperty] private bool _hasRgbConflicts;
    [ObservableProperty] private string _rgbConflictMessage = string.Empty;

    private IAsyncRelayCommand? _closeConflictingAppsCommand;
    public IAsyncRelayCommand CloseConflictingAppsCommand =>
        _closeConflictingAppsCommand ??= new AsyncRelayCommand(CloseConflictingAppsAsync);

    // ── PawnIO ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showPawnIoPrompt;
    [ObservableProperty] private string _pawnIoStatus =
        "Install it once so OpenRGB can reach RGB memory and onboard lighting (GPU lighting works without it).";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PawnIoButtonText))]
    private bool _isInstallingPawnIo;

    public string PawnIoButtonText => IsInstallingPawnIo ? "Installing…" : "Install PawnIO";

    private IAsyncRelayCommand? _installPawnIoCommand;
    public IAsyncRelayCommand InstallPawnIoCommand =>
        _installPawnIoCommand ??= new AsyncRelayCommand(InstallPawnIoAsync, () => !IsInstallingPawnIo);

    // ── Server compatibility ─────────────────────────────────────────────────

    private bool _compatRestartTried;

    /// <summary>Refreshes the conflict and PawnIO banners. Cheap; safe to call on every device refresh.</summary>
    internal void RefreshHardwareAdvisories()
    {
        if (!OperatingSystem.IsWindows()) return;

        var conflicts = RgbConflictDetector.Find();
        bool pawnIoMissing = !PawnIoInstaller.IsInstalled;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _conflicts = conflicts;
            HasRgbConflicts = conflicts.Count > 0;
            RgbConflictMessage = conflicts.Count == 0
                ? string.Empty
                : $"{RgbConflictDetector.Describe(conflicts)} is controlling the same lighting — colours will flicker or be overwritten until it is closed.";
            if (!IsInstallingPawnIo)
                ShowPawnIoPrompt = pawnIoMissing;
        });
    }

    private async Task CloseConflictingAppsAsync()
    {
        var conflicts = _conflicts;
        if (conflicts.Count == 0) return;

        ConnectionStatus = $"Closing {RgbConflictDetector.Describe(conflicts)}…";
        int stopped = await Task.Run(() => RgbConflictDetector.Stop(conflicts));
        ConnectionStatus = $"Closed {stopped} process(es) — rescanning RGB devices…";

        // The devices those apps held only appear after OpenRGB detects again.
        await RestartServerForCompatibilityAsync(force: true);
        RefreshHardwareAdvisories();
    }

    private async Task InstallPawnIoAsync()
    {
        IsInstallingPawnIo = true;
        InstallPawnIoCommand.NotifyCanExecuteChanged();
        try
        {
            var progress = new Progress<string>(m => PawnIoStatus = m);
            var result = await PawnIoInstaller.InstallAsync(progress, _cts.Token);
            PawnIoStatus = result.Message;

            if (result.Success && !result.RebootRequired)
            {
                // OpenRGB only probes SMBus at start-up: restart it so RAM / motherboard RGB appear.
                await RestartServerForCompatibilityAsync(force: true);
            }
        }
        finally
        {
            IsInstallingPawnIo = false;
            InstallPawnIoCommand.NotifyCanExecuteChanged();
            ShowPawnIoPrompt = !PawnIoInstaller.IsInstalled || PawnIoStatus.Contains("restart", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// When the connected OpenRGB server refused FANZi's mode changes, restart it once with FANZi's
    /// compatible config, then reconnect. <paramref name="force"/> restarts regardless (after a driver
    /// install or closing conflicting apps). Returns true when reconnected.
    /// </summary>
    private async Task<bool> RestartServerForCompatibilityAsync(bool force = false)
    {
        // 4.1: never stop or relaunch OpenRGB for a remote endpoint — that PC owns its server.
        if (!EndpointIsLocal)
        {
            ConnectionStatus = $"AEDi: OpenRGB at {EndpointLabel} must be restarted on that PC";
            return false;
        }

        if (!force)
        {
            if (_compatRestartTried || !_rgbService.ModeChangesRejected) return false;
            _compatRestartTried = true;
        }

        ConnectionStatus = force
            ? "AEDi: Restarting OpenRGB to detect all RGB hardware…"
            : "AEDi: OpenRGB refused colour-mode changes — restarting it with FANZI's compatible settings…";
        AppLog.Info($"RGB: {ConnectionStatus}");

        _rgbService.Disconnect();
        bool started = await _serverManager.RestartForCompatibilityAsync(OpenRgbPort, _cts.Token);
        ServerStatus = _serverManager.Status;
        IsServerRunning = started;
        if (!started) return false;

        // OpenRGB opens its SDK socket only after device detection; retry for ~20 s.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (await _rgbService.TryConnectAsync(OpenRgbHost, OpenRgbPort, _cts.Token))
            {
                IsConnected = true;
                ConnectionStatus = $"Connected — {_rgbService.ServerVersion}";
                if (force) await RefreshDevicesAsync();
                return true;
            }
            await Task.Delay(1000, _cts.Token);
        }

        IsConnected = false;
        ConnectionStatus = _rgbService.ServerVersion;
        return false;
    }
}
