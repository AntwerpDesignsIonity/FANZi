using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.ViewModels;

public sealed partial class SystemCleanerViewModel : ViewModelBase, IDisposable
{
    private readonly SystemCleanerService _service = new();
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<CleanTargetItem> Targets { get; } = new();

    [ObservableProperty] private string _totalSelectedSize = "0 MB";
    [ObservableProperty] private string _scanStatus = "Click 'Scan System' to see what can be cleaned";
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private string _ramStatus = "Click 'Trim RAM' to release working sets";
    [ObservableProperty] private bool _isWorking;

    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand CleanSelectedCommand { get; }
    public IAsyncRelayCommand TrimRamCommand { get; }
    public IAsyncRelayCommand EmptyRecycleBinCommand { get; }
    public IAsyncRelayCommand FlushDnsCommand { get; }
    public IAsyncRelayCommand WindowsCleanupCommand { get; }
    public IAsyncRelayCommand OptimiseDriveCommand { get; }
    public IAsyncRelayCommand ResetSysMainCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand SelectNoneCommand { get; }

    public SystemCleanerViewModel()
    {
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        CleanSelectedCommand = new AsyncRelayCommand(CleanSelectedAsync);
        TrimRamCommand = new AsyncRelayCommand(TrimRamAsync);
        EmptyRecycleBinCommand = new AsyncRelayCommand(EmptyRecycleBinAsync);
        FlushDnsCommand = new AsyncRelayCommand(FlushDnsAsync);
        WindowsCleanupCommand = new AsyncRelayCommand(async () =>
        {
            IsWorking = true; await _service.RunWindowsCleanupAsync(); IsWorking = false;
            LastResult = "Windows Disk Cleanup launched";
        });
        OptimiseDriveCommand = new AsyncRelayCommand(async () =>
        {
            IsWorking = true; await _service.OptimiseSystemDriveAsync(); IsWorking = false;
            LastResult = "TRIM/optimise issued to C:";
        });
        ResetSysMainCommand = new AsyncRelayCommand(async () =>
        {
            IsWorking = true; await _service.ResetSysMainAsync(); IsWorking = false;
            LastResult = "SysMain restarted — Superfetch RAM released";
        });
        SelectAllCommand = new RelayCommand(() => { foreach (var t in Targets) t.IsSelected = true; UpdateSelectedSize(); });
        SelectNoneCommand = new RelayCommand(() => { foreach (var t in Targets) t.IsSelected = false; UpdateSelectedSize(); });
    }

    private async Task ScanAsync()
    {
        if (IsWorking) return;
        IsWorking = true;
        ScanStatus = "Scanning... (this can take 5-30 seconds)";
        try
        {
            var found = await _service.ScanAsync(_cts.Token);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Targets.Clear();
                foreach (var t in found.OrderByDescending(t => t.Bytes))
                {
                    var item = new CleanTargetItem(t) { IsSelected = t.Bytes > 0 };
                    item.PropertyChanged += (_, _) => UpdateSelectedSize();
                    Targets.Add(item);
                }
                long total = found.Sum(t => t.Bytes);
                ScanStatus = $"Found {found.Count} targets totalling {total / 1024.0 / 1024.0:F0} MB";
                UpdateSelectedSize();
            });
        }
        finally { IsWorking = false; }
    }

    private async Task CleanSelectedAsync()
    {
        var picked = Targets.Where(t => t.IsSelected).Select(t => t.Target).ToList();
        if (picked.Count == 0) { LastResult = "Nothing selected"; return; }

        IsWorking = true;
        var result = await _service.CleanAsync(picked, _cts.Token);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => LastResult = result.Summary);
        IsWorking = false;
        await ScanAsync();
    }

    private async Task TrimRamAsync()
    {
        IsWorking = true;
        RamStatus = "Trimming working sets...";
        var r = await _service.TrimAllWorkingSetsAsync(_cts.Token);
        RamStatus = r.Summary;
        IsWorking = false;
    }

    private async Task EmptyRecycleBinAsync()
    {
        IsWorking = true;
        await _service.EmptyRecycleBinAsync();
        LastResult = "Recycle Bin emptied";
        IsWorking = false;
    }

    private async Task FlushDnsAsync()
    {
        IsWorking = true;
        bool ok = await _service.FlushDnsCacheAsync();
        LastResult = ok ? "DNS cache flushed" : "DNS flush failed (admin needed?)";
        IsWorking = false;
    }

    private void UpdateSelectedSize()
    {
        long total = Targets.Where(t => t.IsSelected).Sum(t => t.Target.Bytes);
        TotalSelectedSize = $"{total / 1024.0 / 1024.0:F1} MB selected";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

public sealed partial class CleanTargetItem : ObservableObject
{
    public CleanTarget Target { get; }
    [ObservableProperty] private bool _isSelected;

    public string Name => Target.Name;
    public string Category => Target.Category;
    public string Path => Target.Path;
    public string SizeDisplay => Target.SizeDisplay;

    public CleanTargetItem(CleanTarget t) => Target = t;
}
