using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class SystemCleanerViewModel : ViewModelBase, IDisposable
{
    private readonly SystemCleanerService _service = new();
    private readonly DeepCleanerService _deep = new();
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<CleanTargetItem> Targets { get; } = new();
    public ObservableCollection<InstalledProgram> InstalledPrograms { get; } = new();
    public ObservableCollection<StartupEntry> StartupEntries { get; } = new();
    public ObservableCollection<RegistryIssue> RegistryIssues { get; } = new();
    public ObservableCollection<DiskItem> DiskHogs { get; } = new();

    [ObservableProperty] private string _totalSelectedSize = "0 MB";
    [ObservableProperty] private string _scanStatus = "Click 'Scan System' to see what can be cleaned";
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private string _ramStatus = "Click 'Trim RAM' to release working sets";
    [ObservableProperty] private bool _isWorking;

    // Clean confirmation
    [ObservableProperty] private bool _showCleanConfirmation;
    [ObservableProperty] private string _cleanConfirmInput = "";
    [ObservableProperty] private string _cleanConfirmError = "";
    private const string CLEAN_CONFIRM_WORD = "CLEAN";

    // CCleaner-grade additions
    [ObservableProperty] private string _installedProgramsStatus = "Click 'Scan Installed Programs' to load";
    [ObservableProperty] private InstalledProgram? _selectedProgram;
    [ObservableProperty] private string _startupStatus = "Click 'Scan Startup' to load programs that boot with Windows";
    [ObservableProperty] private StartupEntry? _selectedStartup;
    [ObservableProperty] private string _registryStatus = "Click 'Scan Registry' to find broken entries";
    [ObservableProperty] private string _privacyStatus = "Wipe recent docs, jumplists, Win+R history, search history";
    [ObservableProperty] private string _diskAnalyzerStatus = "Click 'Analyze C:' to find biggest space consumers";
    [ObservableProperty] private string _diskAnalyzerRoot = "C:\\";

    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand CleanSelectedCommand { get; }
    public IAsyncRelayCommand ConfirmCleanCommand { get; }
    public IRelayCommand CancelCleanCommand { get; }
    public IAsyncRelayCommand TrimRamCommand { get; }
    public IAsyncRelayCommand EmptyRecycleBinCommand { get; }
    public IAsyncRelayCommand FlushDnsCommand { get; }
    public IAsyncRelayCommand ScanInstalledProgramsCommand { get; }
    public IRelayCommand UninstallSelectedProgramCommand { get; }
    public IAsyncRelayCommand ScanStartupCommand { get; }
    public IRelayCommand DisableSelectedStartupCommand { get; }
    public IRelayCommand DeleteSelectedStartupCommand { get; }
    public IAsyncRelayCommand ScanRegistryCommand { get; }
    public IRelayCommand FixRegistryIssuesCommand { get; }
    public IAsyncRelayCommand WipePrivacyCommand { get; }
    public IAsyncRelayCommand AnalyzeDiskCommand { get; }
    public IAsyncRelayCommand WindowsCleanupCommand { get; }
    public IAsyncRelayCommand OptimiseDriveCommand { get; }
    public IAsyncRelayCommand ResetSysMainCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand SelectNoneCommand { get; }

    public SystemCleanerViewModel()
    {
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        CleanSelectedCommand = new RelayCommand(RequestCleanConfirmation);
        ConfirmCleanCommand = new AsyncRelayCommand(ExecuteCleanAsync);
        CancelCleanCommand = new RelayCommand(CancelClean);
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

        // CCleaner-grade commands
        ScanInstalledProgramsCommand = new AsyncRelayCommand(ScanInstalledProgramsAsync);
        UninstallSelectedProgramCommand = new RelayCommand(UninstallSelectedProgram);
        ScanStartupCommand = new AsyncRelayCommand(ScanStartupAsync);
        DisableSelectedStartupCommand = new RelayCommand(DisableSelectedStartup);
        DeleteSelectedStartupCommand = new RelayCommand(DeleteSelectedStartup);
        ScanRegistryCommand = new AsyncRelayCommand(ScanRegistryAsync);
        FixRegistryIssuesCommand = new RelayCommand(FixRegistryIssues);
        WipePrivacyCommand = new AsyncRelayCommand(WipePrivacyAsync);
        AnalyzeDiskCommand = new AsyncRelayCommand(AnalyzeDiskAsync);
    }

    private async Task ScanInstalledProgramsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsWorking = true;
        InstalledProgramsStatus = "Scanning installed programs...";
        var list = await _deep.ListInstalledProgramsAsync(_cts.Token);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            InstalledPrograms.Clear();
            foreach (var p in list) InstalledPrograms.Add(p);
            InstalledProgramsStatus = $"Found {list.Count} installed programs (sorted by size)";
        });
        IsWorking = false;
    }

    private void UninstallSelectedProgram()
    {
        if (!OperatingSystem.IsWindows() || SelectedProgram is null) return;
        bool ok = _deep.LaunchUninstaller(SelectedProgram);
        InstalledProgramsStatus = ok
            ? $"Launched uninstaller for '{SelectedProgram.DisplayName}'"
            : "Uninstaller could not be launched";
    }

    private async Task ScanStartupAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsWorking = true;
        var list = await _deep.ListStartupEntriesAsync(_cts.Token);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StartupEntries.Clear();
            foreach (var e in list) StartupEntries.Add(e);
            StartupStatus = $"Found {list.Count} startup entries ({list.Count(e => e.IsEnabled)} enabled)";
        });
        IsWorking = false;
    }

    private void DisableSelectedStartup()
    {
        if (!OperatingSystem.IsWindows() || SelectedStartup is null) return;
        bool ok = _deep.ToggleStartupEntry(SelectedStartup, enable: false);
        StartupStatus = ok ? $"Disabled '{SelectedStartup.Name}' at startup" : "Could not disable (registry permission?)";
        _ = ScanStartupAsync();
    }

    private void DeleteSelectedStartup()
    {
        if (!OperatingSystem.IsWindows() || SelectedStartup is null) return;
        bool ok = _deep.DeleteStartupEntry(SelectedStartup);
        StartupStatus = ok ? $"Removed '{SelectedStartup.Name}' from startup" : "Could not remove (admin needed?)";
        _ = ScanStartupAsync();
    }

    private async Task ScanRegistryAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsWorking = true;
        RegistryStatus = "Scanning registry for orphan entries and broken shortcuts...";
        var list = await _deep.ScanRegistryAsync(_cts.Token);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RegistryIssues.Clear();
            foreach (var i in list) RegistryIssues.Add(i);
            RegistryStatus = $"Found {list.Count} registry issues. Review and click 'Fix Selected'.";
        });
        IsWorking = false;
    }

    private void FixRegistryIssues()
    {
        if (!OperatingSystem.IsWindows()) return;
        int fixedCount = _deep.FixRegistryIssues(RegistryIssues.ToList());
        RegistryStatus = $"Fixed {fixedCount} of {RegistryIssues.Count} issues";
        _ = ScanRegistryAsync();
    }

    private async Task WipePrivacyAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsWorking = true;
        PrivacyStatus = "Wiping privacy traces...";
        var r = await _deep.WipePrivacyTracesAsync(_cts.Token);
        PrivacyStatus = r.Summary;
        IsWorking = false;
    }

    private async Task AnalyzeDiskAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsWorking = true;
        DiskAnalyzerStatus = $"Scanning {DiskAnalyzerRoot} for biggest space consumers...";
        var list = await _deep.AnalyzeDiskAsync(DiskAnalyzerRoot, topN: 50, _cts.Token);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DiskHogs.Clear();
            foreach (var d in list) DiskHogs.Add(d);
            DiskAnalyzerStatus = $"Top {list.Count} space consumers in {DiskAnalyzerRoot}";
        });
        IsWorking = false;
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

    private void RequestCleanConfirmation()
    {
        var picked = Targets.Where(t => t.IsSelected).Select(t => t.Target).ToList();
        if (picked.Count == 0) { LastResult = "Nothing selected"; return; }
        ShowCleanConfirmation = true;
        CleanConfirmInput = "";
        CleanConfirmError = "";
    }

    private void CancelClean()
    {
        ShowCleanConfirmation = false;
        CleanConfirmInput = "";
        CleanConfirmError = "";
    }

    private async Task ExecuteCleanAsync()
    {
        if (CleanConfirmInput?.Trim().ToUpperInvariant() != CLEAN_CONFIRM_WORD)
        {
            CleanConfirmError = $"Type '{CLEAN_CONFIRM_WORD}' to confirm deletion";
            return;
        }

        ShowCleanConfirmation = false;
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
