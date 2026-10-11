using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// 4.1 OpenRGB endpoint + control diagnostics:
/// <list type="bullet">
/// <item>the SDK endpoint (host / port) is editable, validated and saved — a PC whose OpenRGB listens on another
/// port, or a second PC on the network running the OpenRGB SDK server, can be driven;</item>
/// <item>remote endpoints are only connected to: FANZi never launches, restarts or kills OpenRGB for them;</item>
/// <item>Auto-detect finds the port a running OpenRGB actually listens on (Windows listener table + common ports);</item>
/// <item>"Take control" re-asserts the per-device control mode and pushes the current lighting at once, and the
/// control report says per device whether FANZi drives it;</item>
/// <item>"Copy diagnostics" collects everything needed to fix RGB on another PC into one text block.</item>
/// </list>
/// </summary>
public sealed partial class RgbControlViewModel
{
    /// <summary>Default OpenRGB SDK port.</summary>
    public const int DefaultOpenRgbPort = 6742;

    [ObservableProperty] private string _endpointHostInput = "127.0.0.1";
    [ObservableProperty] private decimal? _endpointPortInput = DefaultOpenRgbPort;
    [ObservableProperty] private string _endpointStatus = "Local OpenRGB — FANZi starts and manages the SDK server.";
    [ObservableProperty] private bool _isApplyingEndpoint;
    [ObservableProperty] private string _controlReport = string.Empty;

    private Action<string, int>? _persistEndpoint;

    /// <summary>Clipboard writer supplied by the view (TopLevel.Clipboard).</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    private IAsyncRelayCommand? _applyEndpointCommand, _detectEndpointCommand, _takeControlCommand, _copyDiagnosticsCommand;
    public IAsyncRelayCommand ApplyEndpointCommand => _applyEndpointCommand ??= new AsyncRelayCommand(ApplyEndpointAsync, () => !IsApplyingEndpoint);
    public IAsyncRelayCommand DetectEndpointCommand => _detectEndpointCommand ??= new AsyncRelayCommand(DetectEndpointAsync, () => !IsApplyingEndpoint);
    public IAsyncRelayCommand TakeControlCommand => _takeControlCommand ??= new AsyncRelayCommand(TakeControlAsync);
    public IAsyncRelayCommand CopyDiagnosticsCommand => _copyDiagnosticsCommand ??= new AsyncRelayCommand(CopyDiagnosticsAsync);

    partial void OnIsApplyingEndpointChanged(bool value)
    {
        ApplyEndpointCommand.NotifyCanExecuteChanged();
        DetectEndpointCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True when the SDK endpoint is this PC (FANZi may start / restart OpenRGB for it).</summary>
    public bool EndpointIsLocal => IsLocalHost(OpenRgbHost);

    /// <summary>"127.0.0.1:6742" — shown in the endpoint panel.</summary>
    public string EndpointLabel => $"{OpenRgbHost}:{OpenRgbPort}";

    partial void OnOpenRgbHostChanged(string value)
    {
        OnPropertyChanged(nameof(EndpointIsLocal));
        OnPropertyChanged(nameof(EndpointLabel));
    }

    partial void OnOpenRgbPortChanged(int value) => OnPropertyChanged(nameof(EndpointLabel));

    internal static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        host = host.Trim();
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    /// <summary>Validates a host / port pair; returns null when valid, otherwise the reason.</summary>
    internal static string? ValidateEndpoint(string? host, decimal? port)
    {
        if (string.IsNullOrWhiteSpace(host)) return "Enter the OpenRGB host (127.0.0.1 for this PC).";
        host = host.Trim();
        if (host.Contains("://", StringComparison.Ordinal) || host.Contains('/') || host.Contains(' '))
            return "Enter only a host name or IP address (no http://, paths or spaces).";
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return $"'{host}' is not a valid host name or IP address.";
        if (port is not decimal p || p != decimal.Truncate(p) || p < 1 || p > 65535) return "The port must be a whole number from 1 to 65535.";
        return null;
    }

    /// <summary>Applies the saved endpoint (from settings) before the first auto-start; null values keep the defaults.</summary>
    public void ApplyEndpointSettings(string? host, int? port, Action<string, int> persist)
    {
        _persistEndpoint = persist;
        string h = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        int p = port is >= 1 and <= 65535 ? port.Value : DefaultOpenRgbPort;
        if (ValidateEndpoint(h, p) is not null) { h = "127.0.0.1"; p = DefaultOpenRgbPort; }
        OpenRgbHost = h;
        OpenRgbPort = p;
        EndpointHostInput = h;
        EndpointPortInput = p;
        EndpointStatus = DescribeEndpoint();
    }

    private string DescribeEndpoint() => EndpointIsLocal
        ? $"This PC, port {OpenRgbPort} — FANZi starts and manages the OpenRGB SDK server."
        : $"Remote OpenRGB at {OpenRgbHost}:{OpenRgbPort} — enable its SDK server (OpenRGB → SDK Server → Start) and allow port {OpenRgbPort} in that PC's firewall.";

    /// <summary>Is the SDK endpoint reachable right now (local listener table, or a TCP probe for remote hosts)?</summary>
    private bool IsEndpointReachable()
        => EndpointIsLocal
            ? OpenRgbServerManager.IsPortListening(OpenRgbPort)
            : OpenRgbServerManager.IsReachable(OpenRgbHost, OpenRgbPort, 1500);

    private async Task ApplyEndpointAsync()
    {
        if (ValidateEndpoint(EndpointHostInput, EndpointPortInput) is { } error)
        {
            EndpointStatus = error;
            return;
        }

        IsApplyingEndpoint = true;
        try
        {
            string host = EndpointHostInput.Trim();
            int port = (int)EndpointPortInput!.Value;
            bool changed = !host.Equals(OpenRgbHost, StringComparison.OrdinalIgnoreCase) || port != OpenRgbPort;
            bool wasLocal = EndpointIsLocal;

            OpenRgbHost = host;
            OpenRgbPort = port;
            _persistEndpoint?.Invoke(host, port);
            AppLog.Info($"RGB: OpenRGB endpoint set to {host}:{port} ({(EndpointIsLocal ? "local" : "remote")})");

            DisconnectFromServer();
            // Leaving a local endpoint (or changing its port): our own server would hold the hardware — stop it.
            if (changed && wasLocal) { _serverManager.StopServer(); IsServerRunning = false; }

            EndpointStatus = $"Connecting to {EndpointLabel}…";
            LastOpenRgbError = null;
            if (EndpointIsLocal)
            {
                await RetryOpenRgbAsync();
            }
            else
            {
                await ConnectRemoteAsync();
                RestartWatchdog();
            }
            EndpointStatus = IsConnected
                ? $"Connected to {EndpointLabel} — {Devices.Count} device(s). {DescribeEndpoint()}"
                : $"Not connected to {EndpointLabel}. {DescribeEndpoint()}";
        }
        finally
        {
            IsApplyingEndpoint = false;
        }
    }

    /// <summary>Connects to a remote SDK server: no local launch, install or restart.</summary>
    private async Task ConnectRemoteAsync()
    {
        if (!OpenRgbServerManager.IsReachable(OpenRgbHost, OpenRgbPort, 2500))
        {
            IsConnected = false;
            ConnectionStatus = $"AEDi: {EndpointLabel} is not reachable";
            LastOpenRgbError = $"No OpenRGB SDK server answers on {EndpointLabel}. On that PC: OpenRGB → SDK Server → Start Server, " +
                               $"and allow TCP port {OpenRgbPort} through its firewall.";
            return;
        }
        await ConnectAsync();
        if (IsConnected && Devices.Count == 0 && _rgbService.LastDeviceQueryError is null)
            ApplyNoDevicesStatus();
    }

    private void RestartWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = ConnectionWatchdogAsync(_watchdogCts.Token);
    }

    /// <summary>
    /// Finds where OpenRGB's SDK server actually listens: ports owned by running OpenRGB processes (Windows listener
    /// table), then the saved port and 6742–6750. The first port that accepts an SDK handshake wins.
    /// </summary>
    private async Task DetectEndpointAsync()
    {
        IsApplyingEndpoint = true;
        try
        {
            EndpointStatus = "Looking for an OpenRGB SDK server on this PC…";
            var candidates = new List<int>();
            candidates.AddRange(await Task.Run(OpenRgbServerManager.ListOpenRgbListeningPorts));
            candidates.Add(OpenRgbPort);
            candidates.AddRange(Enumerable.Range(DefaultOpenRgbPort, 9));
            foreach (int port in candidates.Distinct())
            {
                if (!OpenRgbServerManager.IsPortListening(port)) continue;
                EndpointStatus = $"Port {port} is open — checking for OpenRGB…";
                if (port == OpenRgbPort && EndpointIsLocal && _rgbService.IsConnected)
                {
                    EndpointStatus = $"Already connected to OpenRGB on this PC, port {port}.";
                    return;
                }
                _rgbService.Disconnect();
                if (await _rgbService.TryConnectAsync("127.0.0.1", port, _cts.Token))
                {
                    EndpointHostInput = "127.0.0.1";
                    EndpointPortInput = port;
                    _rgbService.Disconnect();
                    IsApplyingEndpoint = false;
                    await ApplyEndpointAsync();
                    EndpointStatus = $"Found OpenRGB on this PC, port {port}. " + EndpointStatus;
                    return;
                }
            }
            EndpointStatus = "No OpenRGB SDK server is listening on this PC. Retry starts FANZi's own server on the saved port, " +
                             "or enter another PC's address above.";
            if (EndpointIsLocal && !IsConnected) await AutoStartOpenRgbAsync(userInitiated: true);
        }
        finally
        {
            IsApplyingEndpoint = false;
        }
    }

    /// <summary>
    /// Re-reads the devices (OpenRGB switches each into FANZi's control mode: Direct / Custom / Static), pushes the
    /// current colour immediately and reports, per device, whether FANZi now drives it.
    /// </summary>
    private async Task TakeControlAsync()
    {
        if (!_rgbService.IsConnected)
        {
            ControlReport = $"Not connected to OpenRGB ({EndpointLabel}). Use Retry or Apply & connect first.";
            return;
        }
        if (!RgbEnabled) RgbEnabled = true;
        ControlReport = "Taking control of every RGB device…";
        int count = await RefreshDevicesAsync();
        try { await _rgbService.SetAllDevicesColorAsync(PrimaryColor, _cts.Token); }
        catch (Exception ex) { AppLog.Warn("RGB: take-control colour push failed", ex); }
        ControlReport = BuildControlReport(Devices.ToList(), _rgbService.ModeSwitchStatus, _rgbService.ModeChangesRejected);
        AppLog.Info($"RGB: take control — {count} device(s). {ControlReport.Replace('\n', ' ')}");
    }

    /// <summary>"7 of 9 devices under FANZi control …" plus one line per device that is not.</summary>
    internal static string BuildControlReport(IReadOnlyList<RgbDeviceInfo> devices, string modeStatus, bool modeChangesRejected)
    {
        if (devices.Count == 0) return "OpenRGB reports no devices — use Deep scan to see which hardware is missing and why.";
        var controllable = devices.Where(d => d.IsControllable).ToList();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{controllable.Count} of {devices.Count} device(s) under FANZi control");
        if (!string.IsNullOrWhiteSpace(modeStatus)) sb.Append(" — ").Append(modeStatus);
        sb.Append('.');
        foreach (var d in devices.Where(d => !d.IsControllable))
            sb.Append('\n').Append(CultureInfo.InvariantCulture, $"• {d.Name}: effects-only in OpenRGB (no Direct / Custom / Static mode) — it keeps its own lighting.");
        if (modeChangesRejected)
            sb.Append("\n• The OpenRGB server refused mode changes (legacy_workaround off). Restart OpenRGB from FANZi, or enable it in OpenRGB → Settings → Server.");
        return sb.ToString();
    }

    private async Task CopyDiagnosticsAsync()
    {
        string text = BuildDiagnostics();
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FANZI");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "rgb-diagnostics.txt");
            await System.IO.File.WriteAllTextAsync(path, text);
            if (CopyToClipboard is not null) await CopyToClipboard(text);
            ControlReport = $"RGB diagnostics copied to the clipboard and saved to {path}";
        }
        catch (Exception ex)
        {
            ControlReport = $"Could not save the diagnostics: {ex.Message}";
        }
    }

    /// <summary>Everything needed to troubleshoot RGB on another PC, as plain text.</summary>
    internal string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FANZi IO-nity {AppVersion.Display} — RGB diagnostics {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Elevated: {IsElevated()}");
        sb.AppendLine($"Endpoint: {EndpointLabel} ({(EndpointIsLocal ? "local" : "remote")}), reachable: {IsEndpointReachable()}");
        sb.AppendLine($"RGB enabled: {RgbEnabled}; connected: {_rgbService.IsConnected}; server: {_rgbService.ServerVersion}");
        sb.AppendLine($"Server status: {ServerStatus}");
        sb.AppendLine($"OpenRGB path: {_serverManager.DetectedPath ?? "not found"} (custom: {_serverManager.CustomPath ?? "none"})");
        sb.AppendLine($"OpenRGB processes / listening ports: {string.Join(", ", OpenRgbServerManager.ListOpenRgbListeningPorts())}");
        sb.AppendLine($"Mode control: {_rgbService.ModeSwitchStatus}; mode changes rejected: {_rgbService.ModeChangesRejected}");
        if (_rgbService.LastDeviceQueryError is { } q) sb.AppendLine($"Device query error: {q}");
        if (!string.IsNullOrWhiteSpace(LastOpenRgbError)) sb.AppendLine($"Last error: {LastOpenRgbError}");
        if (HasRgbConflicts) sb.AppendLine($"Conflicts: {RgbConflictMessage}");
        sb.AppendLine($"PawnIO: {PawnIoStatus}");
        sb.AppendLine($"Devices ({Devices.Count}):");
        foreach (var d in Devices)
            sb.AppendLine($"  [{d.DeviceIndex}] {d.Name} · {d.FriendlyType} · {d.Vendor} · LEDs {d.LedCount} · zones {d.Zones.Count} · {(d.IsControllable ? "controllable" : "effects-only")} · {d.ControlLabel} · {d.Location}");
        if (!string.IsNullOrWhiteSpace(ControlReport)) sb.AppendLine("Control report: " + ControlReport.Replace("\n", " | "));
        return sb.ToString();
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return Environment.UserName == "root";
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
    private string? _lastFrameFailure;
    private DateTime _lastFrameFailureAt;

    /// <summary>
    /// Logs a failed hardware frame (once per distinct message per minute). These were swallowed silently before
    /// 4.1, which hid exactly the errors needed to fix "colours do not apply" on another PC.
    /// </summary>
    private void LogFrameFailure(Exception ex)
    {
        string key = ex.GetType().Name + ": " + ex.Message;
        if (key == _lastFrameFailure && DateTime.UtcNow - _lastFrameFailureAt < TimeSpan.FromMinutes(1)) return;
        _lastFrameFailure = key;
        _lastFrameFailureAt = DateTime.UtcNow;
        AppLog.Warn($"RGB: frame write to OpenRGB failed — {key}");
    }
}
