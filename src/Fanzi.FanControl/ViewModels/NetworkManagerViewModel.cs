using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace Fanzi.FanControl.ViewModels;

public sealed partial class NetworkManagerViewModel : ViewModelBase, IDisposable
{
    private readonly NetworkMonitorService _service = new();
    private readonly PortScannerService _portScanner = new();
    private readonly System.Timers.Timer _timer;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<NetworkAdapterStats> Adapters { get; } = new();
    public ObservableCollection<ProcessNetworkStats> ActiveProcesses { get; } = new();
    public ObservableCollection<PortScanResult> PortResults { get; } = new();
    public ObservableCollection<ListeningPort> ListeningPorts { get; } = new();
    public ObservableCollection<ProcessGroup> ProcessGroups { get; } = new();

    [ObservableProperty] private string _totalDownloadRate = "0 KB/s";
    [ObservableProperty] private string _totalUploadRate = "0 KB/s";
    [ObservableProperty] private string _lastUpdated = "";
    [ObservableProperty] private string _rateLimitAppName = "";
    [ObservableProperty] private int _rateLimitKbps = 1000;
    [ObservableProperty] private string _rateLimitStatus = "";

    // Port scanner
    [ObservableProperty] private string _scanHost = "127.0.0.1";
    [ObservableProperty] private int _scanStartPort = 1;
    [ObservableProperty] private int _scanEndPort = 1024;
    [ObservableProperty] private string _portScanStatus = "Ready";
    [ObservableProperty] private bool _isScanning;

    // Speed limits per process
    [ObservableProperty] private string _speedLimitProcess = "";
    [ObservableProperty] private int _downloadLimitKbps = 0;
    [ObservableProperty] private int _uploadLimitKbps = 0;
    [ObservableProperty] private string _speedLimitStatus = "";

    // Process groups
    [ObservableProperty] private string _newGroupName = "";
    [ObservableProperty] private string _newGroupProcesses = "";
    [ObservableProperty] private ProcessGroup? _selectedGroup;

    public IRelayCommand RefreshNowCommand { get; }
    public IRelayCommand OpenNetworkAdaptersCommand { get; }
    public IRelayCommand ApplyRateLimitCommand { get; }
    public IRelayCommand RemoveRateLimitCommand { get; }
    public IAsyncRelayCommand ScanPortsCommand { get; }
    public IRelayCommand RefreshListeningPortsCommand { get; }
    public IRelayCommand ClosePortCommand { get; }
    public IRelayCommand ApplySpeedLimitCommand { get; }
    public IRelayCommand RemoveSpeedLimitCommand { get; }
    public IRelayCommand AddProcessGroupCommand { get; }
    public IRelayCommand RemoveProcessGroupCommand { get; }
    public IRelayCommand ApplyGroupLimitsCommand { get; }

    public NetworkManagerViewModel()
    {
        RefreshNowCommand = new RelayCommand(() => _ = RefreshAsync());
        OpenNetworkAdaptersCommand = new RelayCommand(PowerMonitorService.OpenNetworkAdapters);
        ApplyRateLimitCommand = new RelayCommand(ApplyLimit);
        RemoveRateLimitCommand = new RelayCommand(RemoveLimit);
        ScanPortsCommand = new AsyncRelayCommand(ScanPortsAsync);
        RefreshListeningPortsCommand = new RelayCommand(RefreshListeningPorts);
        ClosePortCommand = new RelayCommand<PortScanResult>(ClosePort);
        ApplySpeedLimitCommand = new RelayCommand(ApplySpeedLimit);
        RemoveSpeedLimitCommand = new RelayCommand(RemoveSpeedLimit);
        AddProcessGroupCommand = new RelayCommand(AddProcessGroup);
        RemoveProcessGroupCommand = new RelayCommand(RemoveProcessGroup);
        ApplyGroupLimitsCommand = new RelayCommand<ProcessGroup>(ApplyGroupLimits);

        // Initialize premade groups
        ProcessGroups.Add(new ProcessGroup("Browsers", "chrome.exe;edge.exe;firefox.exe;brave.exe", 0, 0));
        ProcessGroups.Add(new ProcessGroup("Gaming", "FortniteClient-Win64-Shipping.exe;VALORANT-Win64-Shipping.exe;cs2.exe;EldenRing.exe", 0, 0));
        ProcessGroups.Add(new ProcessGroup("Streaming", "obs64.exe;Streamlabs.exe;XSplit.exe", 5000, 2000));
        ProcessGroups.Add(new ProcessGroup("Downloads", "torrent.exe;dropbox.exe;onedrive.exe", 10000, 1000));
        ProcessGroups.Add(new ProcessGroup("Communication", "teams.exe;zoom.exe;discord.exe;slack.exe", 2000, 1000));

        _timer = new System.Timers.Timer(2000) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
        RefreshListeningPorts();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var adapters = await _service.GetAdaptersAsync(_cts.Token);
            var procs = await _service.GetProcessNetworkActivityAsync(_cts.Token);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Adapters.Clear();
                double totalDl = 0, totalUl = 0;
                foreach (var a in adapters)
                {
                    Adapters.Add(a);
                    totalDl += a.DownloadBps;
                    totalUl += a.UploadBps;
                }

                ActiveProcesses.Clear();
                foreach (var p in procs) ActiveProcesses.Add(p);

                TotalDownloadRate = FormatBps(totalDl);
                TotalUploadRate = FormatBps(totalUl);
                LastUpdated = $"Updated {DateTime.Now:HH:mm:ss}";
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void ApplyLimit()
    {
        if (string.IsNullOrWhiteSpace(RateLimitAppName)) { RateLimitStatus = "Enter an exe name (e.g. chrome.exe)"; return; }
        bool ok = _service.ApplyRateLimit(RateLimitAppName, RateLimitKbps);
        RateLimitStatus = ok
            ? $"Limit applied: {RateLimitAppName} → {RateLimitKbps} kbps"
            : "Failed (need admin; try running FANZi as administrator)";
    }

    private void RemoveLimit()
    {
        if (string.IsNullOrWhiteSpace(RateLimitAppName)) { RateLimitStatus = "Enter an exe name first"; return; }
        bool ok = _service.RemoveRateLimit(RateLimitAppName);
        RateLimitStatus = ok ? $"Limit removed for {RateLimitAppName}" : "Failed to remove";
    }

    // ── Port Scanner ──────────────────────────────────────────────────────

    private async Task ScanPortsAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        PortScanStatus = $"Scanning {ScanHost}:{ScanStartPort}-{ScanEndPort}...";
        PortResults.Clear();

        try
        {
            var results = await _portScanner.ScanAsync(ScanHost, ScanStartPort, ScanEndPort, 200, _cts.Token);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var r in results) PortResults.Add(r);
                int open = results.Count(r => r.State == PortState.Open);
                PortScanStatus = $"Scan complete: {open} open ports found (scanned {ScanEndPort - ScanStartPort + 1} ports)";
            });
        }
        catch (Exception ex)
        {
            PortScanStatus = $"Scan failed: {ex.Message}";
        }
        finally { IsScanning = false; }
    }

    private void RefreshListeningPorts()
    {
        var ports = _portScanner.GetListeningPorts();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ListeningPorts.Clear();
            foreach (var p in ports)
                ListeningPorts.Add(new ListeningPort(p.Port, p.ProcessName ?? "System", p.ProcessId));
        });
    }

    private void ClosePort(PortScanResult? port)
    {
        if (port is null) return;
        bool ok = _portScanner.ClosePort(port.Port);
        PortScanStatus = ok
            ? $"Port {port.Port} closed (process terminated)"
            : $"Could not close port {port.Port} (admin rights needed)";
        RefreshListeningPorts();
    }

    // ── Speed Limits ──────────────────────────────────────────────────────

    private void ApplySpeedLimit()
    {
        if (string.IsNullOrWhiteSpace(SpeedLimitProcess)) { SpeedLimitStatus = "Enter a process name"; return; }
        bool dlOk = true, ulOk = true;
        if (DownloadLimitKbps > 0)
            dlOk = _service.ApplyRateLimit(SpeedLimitProcess, DownloadLimitKbps);
        if (UploadLimitKbps > 0)
            ulOk = _service.ApplyRateLimit($"{SpeedLimitProcess}-upload", UploadLimitKbps);

        SpeedLimitStatus = (dlOk && ulOk)
            ? $"Speed limit applied: {SpeedLimitProcess} ↓{DownloadLimitKbps}kbps ↑{UploadLimitKbps}kbps"
            : "Failed (need admin)";
    }

    private void RemoveSpeedLimit()
    {
        if (string.IsNullOrWhiteSpace(SpeedLimitProcess)) return;
        _service.RemoveRateLimit(SpeedLimitProcess);
        _service.RemoveRateLimit($"{SpeedLimitProcess}-upload");
        SpeedLimitStatus = $"Speed limits removed for {SpeedLimitProcess}";
    }

    // ── Process Groups ────────────────────────────────────────────────────

    private void AddProcessGroup()
    {
        if (string.IsNullOrWhiteSpace(NewGroupName)) return;
        ProcessGroups.Add(new ProcessGroup(NewGroupName, NewGroupProcesses, DownloadLimitKbps, UploadLimitKbps));
        NewGroupName = "";
        NewGroupProcesses = "";
    }

    private void RemoveProcessGroup()
    {
        if (SelectedGroup is null) return;
        ProcessGroups.Remove(SelectedGroup);
        SelectedGroup = null;
    }

    private void ApplyGroupLimits(ProcessGroup? group)
    {
        if (group is null) return;
        var processes = group.ProcessList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int applied = 0;
        foreach (var proc in processes)
        {
            if (group.DownloadLimitKbps > 0)
                _service.ApplyRateLimit(proc, group.DownloadLimitKbps);
            applied++;
        }
        SpeedLimitStatus = $"Applied limits to {applied} processes in '{group.Name}'";
    }

    private static string FormatBps(double bps)
    {
        if (bps < 1024) return $"{bps:F0} B/s";
        if (bps < 1024 * 1024) return $"{bps / 1024:F1} KB/s";
        if (bps < 1024 * 1024 * 1024) return $"{bps / 1024 / 1024:F2} MB/s";
        return $"{bps / 1024 / 1024 / 1024:F2} GB/s";
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }
}

public sealed partial class ProcessGroup : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _processList;
    [ObservableProperty] private int _downloadLimitKbps;
    [ObservableProperty] private int _uploadLimitKbps;

    public ProcessGroup(string name, string processList, int dlLimit, int ulLimit)
    {
        _name = name;
        _processList = processList;
        _downloadLimitKbps = dlLimit;
        _uploadLimitKbps = ulLimit;
    }
}

public sealed record ListeningPort(int Port, string ProcessName, int? ProcessId)
{
    public string Display => $"Port {Port} — {ProcessName}{(ProcessId.HasValue ? $" (PID {ProcessId})" : "")}";
}
