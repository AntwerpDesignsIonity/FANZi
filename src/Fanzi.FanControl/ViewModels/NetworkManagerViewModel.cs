using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace Fanzi.FanControl.ViewModels;

public sealed partial class NetworkManagerViewModel : ViewModelBase, IDisposable
{
    private readonly NetworkMonitorService _service = new();
    private readonly System.Timers.Timer _timer;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<NetworkAdapterStats> Adapters { get; } = new();
    public ObservableCollection<ProcessNetworkStats> ActiveProcesses { get; } = new();

    [ObservableProperty] private string _totalDownloadRate = "0 KB/s";
    [ObservableProperty] private string _totalUploadRate = "0 KB/s";
    [ObservableProperty] private string _lastUpdated = "";
    [ObservableProperty] private string _rateLimitAppName = "";
    [ObservableProperty] private int _rateLimitKbps = 1000;
    [ObservableProperty] private string _rateLimitStatus = "";

    public IRelayCommand RefreshNowCommand { get; }
    public IRelayCommand OpenNetworkAdaptersCommand { get; }
    public IRelayCommand ApplyRateLimitCommand { get; }
    public IRelayCommand RemoveRateLimitCommand { get; }

    public NetworkManagerViewModel()
    {
        RefreshNowCommand = new RelayCommand(() => _ = RefreshAsync());
        OpenNetworkAdaptersCommand = new RelayCommand(PowerMonitorService.OpenNetworkAdapters);
        ApplyRateLimitCommand = new RelayCommand(ApplyLimit);
        RemoveRateLimitCommand = new RelayCommand(RemoveLimit);

        _timer = new System.Timers.Timer(2000) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
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
            : "Failed (need admin; try running FANZI as administrator)";
    }

    private void RemoveLimit()
    {
        if (string.IsNullOrWhiteSpace(RateLimitAppName)) { RateLimitStatus = "Enter an exe name first"; return; }
        bool ok = _service.RemoveRateLimit(RateLimitAppName);
        RateLimitStatus = ok ? $"Limit removed for {RateLimitAppName}" : "Failed to remove (already absent or permission denied)";
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
