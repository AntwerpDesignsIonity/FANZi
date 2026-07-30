using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace Fanzi.FanControl.ViewModels;

public sealed partial class TaskManagerViewModel : ViewModelBase, IDisposable
{
    private readonly ProcessMonitorService _service = new();
    private readonly System.Timers.Timer _timer;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<ProcessInfoItem> Processes { get; } = new();
    public ObservableCollection<ProcessInfoItem> SelectedProcesses { get; } = new();

    [ObservableProperty] private ProcessInfoItem? _selectedProcess;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private int _processCount;
    [ObservableProperty] private double _totalCpuPercent;
    [ObservableProperty] private double _totalMemoryGb;
    [ObservableProperty] private string _lastUpdated = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _endTaskStatus = "";

    public IRelayCommand RefreshNowCommand { get; }
    public IRelayCommand KillSelectedCommand { get; }
    public IRelayCommand KillAllSelectedCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand SelectNoneCommand { get; }
    public IRelayCommand OpenWindowsTaskMgrCommand { get; }
    public IRelayCommand OpenResourceMonitorCommand { get; }

    public TaskManagerViewModel()
    {
        RefreshNowCommand = new RelayCommand(() => _ = RefreshAsync());
        KillSelectedCommand = new RelayCommand(KillSelected, () => SelectedProcess is not null);
        KillAllSelectedCommand = new RelayCommand(KillAllSelected, () => SelectedCount > 0);
        SelectAllCommand = new RelayCommand(SelectAll);
        SelectNoneCommand = new RelayCommand(SelectNone);
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
                var currentSelection = Processes.Where(p => p.IsSelected).Select(p => p.Pid).ToHashSet();

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
                        var item = new ProcessInfoItem(p) { IsSelected = currentSelection.Contains(p.Pid) };
                        item.PropertyChanged += OnItemSelectionChanged;
                        Processes.Add(item);
                    }
                }
                ProcessCount = procs.Count;
                TotalCpuPercent = Math.Round(totalCpu, 1);
                TotalMemoryGb = Math.Round(totalMem / 1024.0, 2);
                LastUpdated = $"Updated {DateTime.Now:HH:mm:ss}";
                UpdateSelectedCount();
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void OnItemSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessInfoItem.IsSelected))
            UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = Processes.Count(p => p.IsSelected);
        KillAllSelectedCommand.NotifyCanExecuteChanged();
        EndTaskStatus = SelectedCount > 0 ? $"{SelectedCount} process(es) selected" : "";
    }

    private void SelectAll()
    {
        foreach (var p in Processes) p.IsSelected = true;
    }

    private void SelectNone()
    {
        foreach (var p in Processes) p.IsSelected = false;
    }

    partial void OnFilterChanged(string value) => _ = RefreshAsync();
    partial void OnSelectedProcessChanged(ProcessInfoItem? value) => KillSelectedCommand.NotifyCanExecuteChanged();

    private void KillSelected()
    {
        if (SelectedProcess is null) return;
        bool ok = _service.KillProcess(SelectedProcess.Pid);
        EndTaskStatus = ok ? $"Ended: {SelectedProcess.Name} (PID {SelectedProcess.Pid})" : $"Could not end {SelectedProcess.Name}";
        SelectedProcess.IsSelected = false;
        _ = RefreshAsync();
    }

    private void KillAllSelected()
    {
        var selected = Processes.Where(p => p.IsSelected).ToList();
        int killed = 0;
        foreach (var p in selected)
        {
            if (_service.KillProcess(p.Pid)) killed++;
        }
        EndTaskStatus = $"Ended {killed} of {selected.Count} process(es)";
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

public sealed partial class ProcessInfoItem : ObservableObject
{
    public ProcessInfo Info { get; }
    [ObservableProperty] private bool _isSelected;

    public int Pid => Info.Pid;
    public string Name => Info.Name;
    public string Description => Info.Description;
    public double CpuPercent => Info.CpuPercent;
    public double MemoryMb => Info.MemoryMb;
    public int Threads => Info.Threads;
    public int Handles => Info.Handles;

    public ProcessInfoItem(ProcessInfo info) => Info = info;
}
