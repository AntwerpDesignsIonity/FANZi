using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace Fanzi.FanControl.ViewModels;

public sealed partial class TaskManagerViewModel : ViewModelBase, IDisposable
{
    private readonly ProcessMonitorService _service = new();
    private readonly System.Timers.Timer _timer;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<ProcessInfo> Processes { get; } = new();

    [ObservableProperty] private ProcessInfo? _selectedProcess;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private int _processCount;
    [ObservableProperty] private double _totalCpuPercent;
    [ObservableProperty] private double _totalMemoryGb;
    [ObservableProperty] private string _lastUpdated = "";

    public IRelayCommand RefreshNowCommand { get; }
    public IRelayCommand KillSelectedCommand { get; }
    public IRelayCommand OpenWindowsTaskMgrCommand { get; }
    public IRelayCommand OpenResourceMonitorCommand { get; }

    public TaskManagerViewModel()
    {
        RefreshNowCommand = new RelayCommand(() => _ = RefreshAsync());
        KillSelectedCommand = new RelayCommand(KillSelected, () => SelectedProcess is not null);
        OpenWindowsTaskMgrCommand = new RelayCommand(PowerMonitorService.OpenTaskManager);
        OpenResourceMonitorCommand = new RelayCommand(PowerMonitorService.OpenResourceMonitor);

        _timer = new System.Timers.Timer(2000) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var procs = await _service.GetProcessesAsync(_cts.Token);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Processes.Clear();
                double totalCpu = 0;
                double totalMem = 0;
                foreach (var p in procs)
                {
                    totalCpu += p.CpuPercent;
                    totalMem += p.MemoryMb;
                    if (string.IsNullOrWhiteSpace(Filter) ||
                        p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
                    {
                        Processes.Add(p);
                    }
                }
                ProcessCount = procs.Count;
                TotalCpuPercent = Math.Round(totalCpu, 1);
                TotalMemoryGb = Math.Round(totalMem / 1024.0, 2);
                LastUpdated = $"Updated {DateTime.Now:HH:mm:ss}";
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    partial void OnFilterChanged(string value) => _ = RefreshAsync();
    partial void OnSelectedProcessChanged(ProcessInfo? value) => KillSelectedCommand.NotifyCanExecuteChanged();

    private void KillSelected()
    {
        if (SelectedProcess is null) return;
        _service.KillProcess(SelectedProcess.Pid);
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }
}
