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

        _timer = new System.Timers.Timer(3000) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snap = await _service.GetSnapshotAsync(_cts.Token);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Components.Clear();
                foreach (var c in snap.Components) Components.Add(c);

                Drives.Clear();
                foreach (var d in snap.Drives) Drives.Add(d);

                TotalComponentWatts = $"{snap.TotalComponentWatts:F1} W";
                TotalSystemWatts = $"{snap.EstimatedSystemWatts:F1} W (estimated)";
                LastUpdated = $"Updated {DateTime.Now:HH:mm:ss}";
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _service.Dispose();
    }
}
