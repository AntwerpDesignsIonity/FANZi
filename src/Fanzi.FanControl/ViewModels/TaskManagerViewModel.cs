using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
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

        // v4: refreshed by the shared live-sync heartbeat (same frame as the dashboard); the timer only
        // takes over when nothing drives the hub (tests, dashboard loop paused).
        _liveSync = LiveSyncHub.Shared.Register("Tasks", CollectAsync);
        _timer = new System.Timers.Timer(4000) { AutoReset = true };
        _timer.Elapsed += (_, _) => { if (!LiveSyncHub.Shared.IsDriven) _ = RefreshAsync(); };
        _timer.Start();
        _ = RefreshAsync();
    }

    private readonly IDisposable _liveSync;
    private IReadOnlyList<ProcessInfo> _lastProcs = Array.Empty<ProcessInfo>();

    private async Task<Action?> CollectAsync(CancellationToken token)
    {
        var procs = await _service.GetProcessesAsync(token).ConfigureAwait(false);
        return () => Apply(procs);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var procs = await _service.GetProcessesAsync(_cts.Token);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Apply(procs));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { AppLog.Warn("Task manager refresh failed", ex); }
    }

    /// <summary>Applies one sample in place (UI thread): rows keep their selection, scroll position and hover.</summary>
    private void Apply(IReadOnlyList<ProcessInfo> procs)
    {
        _lastProcs = procs;
        double totalCpu = 0;
        double totalMem = 0;
        var visible = new List<ProcessInfoItem>(procs.Count);
        var existing = Processes.ToDictionary(p => p.Pid);
        foreach (var p in procs)
        {
            totalCpu += p.CpuPercent;
            totalMem += p.MemoryMb;
            if (!string.IsNullOrWhiteSpace(Filter) && !p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!existing.TryGetValue(p.Pid, out var item))
            {
                item = new ProcessInfoItem(p);
                item.PropertyChanged += OnItemSelectionChanged;
            }
            visible.Add(item);
        }

        var byPid = procs.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        CollectionSync.Sync(Processes, visible, p => p.Pid, (current, _) => current.UpdateFrom(byPid[current.Pid]));
        var kept = visible.ToHashSet();
        foreach (var gone in existing.Values.Where(i => !kept.Contains(i)))
            gone.PropertyChanged -= OnItemSelectionChanged;

        ProcessCount = procs.Count;
        TotalCpuPercent = Math.Round(totalCpu, 1);
        TotalMemoryGb = Math.Round(totalMem / 1024.0, 2);
        LastUpdated = $"Live · {DateTime.Now:HH:mm:ss}";
        UpdateSelectedCount();
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

    // Instant: re-filter the last sample instead of waiting for the next poll.
    partial void OnFilterChanged(string value) => Apply(_lastProcs);
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
        _liveSync.Dispose();
        _timer.Stop();
        _timer.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }
}

public sealed partial class ProcessInfoItem : ObservableObject
{
    /// <summary>Latest sample; replaced in place each live-sync cycle so the row (and its selection) survives.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Description), nameof(CpuPercent), nameof(MemoryMb), nameof(Threads), nameof(Handles))]
    private ProcessInfo _info;

    [ObservableProperty] private bool _isSelected;

    public int Pid => Info.Pid;
    public string Name => Info.Name;
    public string Description => Info.Description;
    public double CpuPercent => Info.CpuPercent;
    public double MemoryMb => Info.MemoryMb;
    public int Threads => Info.Threads;
    public int Handles => Info.Handles;

    public ProcessInfoItem(ProcessInfo info) => _info = info;

    /// <summary>Takes the newer sample; raises change notifications only when something differs.</summary>
    public void UpdateFrom(ProcessInfo info)
    {
        if (!Equals(Info, info)) Info = info;
    }
}
