using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace Fanzi.FanControl.ViewModels;

public sealed partial class PowerMonitorViewModel : ViewModelBase, IDisposable
{
    private readonly PowerMonitorService _service = new();
    private readonly System.Timers.Timer _timer;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<ComponentPower> Components { get; } = new();
    public ObservableCollection<DriveInfoStats> Drives { get; } = new();

    [ObservableProperty] private string _totalSystemWatts = "—";
    [ObservableProperty] private string _totalComponentWatts = "—";
    [ObservableProperty] private string _lastUpdated = "";

    public IRelayCommand RefreshNowCommand { get; }
    public IRelayCommand OpenDeviceManagerCommand { get; }
    public IRelayCommand OpenDiskManagementCommand { get; }
    public IRelayCommand OpenPowerOptionsCommand { get; }

    public PowerMonitorViewModel()
    {
        RefreshNowCommand = new RelayCommand(() => _ = RefreshAsync());
        OpenDeviceManagerCommand = new RelayCommand(PowerMonitorService.OpenDeviceManager);
        OpenDiskManagementCommand = new RelayCommand(PowerMonitorService.OpenDiskManagement);
        OpenPowerOptionsCommand = new RelayCommand(PowerMonitorService.OpenPowerOptions);

        // v4: one heartbeat with the dashboard (LiveSyncHub); the timer is only a fallback.
        _liveSync = LiveSyncHub.Shared.Register("Power", CollectAsync, LiveSyncHub.PanelInterval);
        _timer = new System.Timers.Timer(5000) { AutoReset = true };
        _timer.Elapsed += (_, _) => { if (!LiveSyncHub.Shared.IsDriven) _ = RefreshAsync(); };
        _timer.Start();
        _ = RefreshAsync();
    }

    private readonly IDisposable _liveSync;

    private async Task<Action?> CollectAsync(CancellationToken token)
    {
        var snap = await _service.GetSnapshotAsync(token).ConfigureAwait(false);
        return () => Apply(snap);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snap = await _service.GetSnapshotAsync(_cts.Token);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Apply(snap));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { AppLog.Warn("Power refresh failed", ex); }
    }

    /// <summary>Applies one sample in place (UI thread) - rows are diffed, never cleared.</summary>
    private void Apply(PowerSnapshot snap)
    {
        CollectionSync.Sync(Components, snap.Components, c => c.Category + "|" + c.Name);
        CollectionSync.Sync(Drives, snap.Drives, d => d.Letter);

        TotalComponentWatts = $"{snap.TotalComponentWatts:F1} W";
        TotalSystemWatts = $"{snap.EstimatedSystemWatts:F1} W (estimated)";
        LastUpdated = $"Live · {DateTime.Now:HH:mm:ss}";
    }

    public void Dispose()
    {
        _liveSync.Dispose();
        _timer.Stop();
        _timer.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _service.Dispose();
    }
}
