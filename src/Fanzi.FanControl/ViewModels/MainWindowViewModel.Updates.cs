using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// Release check and update. A title-bar badge appears when a newer build is published; Settings → Updates has
/// Check now, Update now (Windows: download, verify against the release's SHA256SUMS.txt, swap in place, restart)
/// and the switch for the automatic check.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);

    private readonly AutoUpdateService _updateService = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateBadgeText = string.Empty;
    [ObservableProperty] private string _updateTooltip = string.Empty;

    /// <summary>One line in Settings → Updates: last result of the check / progress of the update.</summary>
    [ObservableProperty] private string _updateStatusText = $"You have v{AppVersion.Display}. Not checked yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    private bool _isCheckingForUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    private bool _isInstallingUpdate;

    /// <summary>Download progress, 0–100.</summary>
    [ObservableProperty] private double _updateProgress;

    /// <summary>Check GitHub for a newer release 30 s after start and every 12 hours (persisted).</summary>
    [ObservableProperty] private bool _autoUpdateCheckEnabled = true;

    public bool CanInstallUpdate => IsUpdateAvailable && !IsInstallingUpdate && !IsCheckingForUpdate;

    /// <summary>In-place update exists on Windows; elsewhere the button opens the download page.</summary>
    public static bool SelfUpdateSupported => OperatingSystem.IsWindows();

    public string UpdateButtonText => IsInstallingUpdate ? "Updating…"
        : !IsUpdateAvailable ? "Up to date"
        : SelfUpdateSupported ? $"Update now to v{_updateService.LatestVersion}"
        : $"Get v{_updateService.LatestVersion}";

    private IRelayCommand? _openUpdatePageCommand;
    public IRelayCommand OpenUpdatePageCommand => _openUpdatePageCommand ??= new RelayCommand(OpenUpdatePage);

    private IRelayCommand? _showUpdatesCommand;
    /// <summary>Title-bar badge: jump to Settings → Updates.</summary>
    public IRelayCommand ShowUpdatesCommand => _showUpdatesCommand ??= new RelayCommand(() => SelectedTabIndex = SettingsTabIndex);

    private IAsyncRelayCommand? _checkForUpdatesCommand;
    public IAsyncRelayCommand CheckForUpdatesCommand => _checkForUpdatesCommand ??=
        new AsyncRelayCommand(() => SafeExecuteAsync(() => CheckForUpdatesAsync(userInitiated: true, _disposeTokenSource.Token), "Update check"));

    private IAsyncRelayCommand? _installUpdateCommand;
    public IAsyncRelayCommand InstallUpdateCommand => _installUpdateCommand ??=
        new AsyncRelayCommand(() => SafeExecuteAsync(() => InstallUpdateAsync(_disposeTokenSource.Token), "Update"));

    partial void OnAutoUpdateCheckEnabledChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.AutoUpdateCheck = value;
        _ = SaveSettingsAsync();
        UpdateStatusText = value
            ? "Automatic check is on: 30 s after start and every 12 hours."
            : "Automatic check is off. Use Check now.";
    }

    /// <summary>Started from the public constructor only, so tests and the render harness stay offline.</summary>
    private void StartUpdateChecks()
    {
        CancellationToken token;
        try { token = _disposeTokenSource.Token; }
        catch (ObjectDisposedException) { return; }

        _ = SafeExecuteAsync(() => RunUpdateLoopAsync(token), "Update check");
    }

    private async Task RunUpdateLoopAsync(CancellationToken token)
    {
        await Task.Delay(FirstUpdateCheckDelay, token).ConfigureAwait(false);
        while (!token.IsCancellationRequested)
        {
            if (AutoUpdateCheckEnabled && !IsInstallingUpdate)
                await CheckForUpdatesAsync(userInitiated: false, token).ConfigureAwait(false);
            await Task.Delay(UpdateCheckInterval, token).ConfigureAwait(false);
        }
    }

    private async Task CheckForUpdatesAsync(bool userInitiated, CancellationToken token)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsCheckingForUpdate = true;
            if (userInitiated) UpdateStatusText = "Checking GitHub for the latest release…";
        });

        bool available = false;
        try
        {
            available = await _updateService.CheckForUpdateAsync(token).ConfigureAwait(false);
            AppLog.Info($"Update check: {_updateService.Status}");
        }
        finally
        {
            string version = _updateService.LatestVersion ?? "";
            string status = _updateService.Status;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                IsCheckingForUpdate = false;
                IsUpdateAvailable = available;
                UpdateBadgeText = available ? $"Update v{version}" : string.Empty;
                UpdateTooltip = available
                    ? $"FANZi IO-nity v{version} is available (you have v{AppVersion.Display}). Click for Settings → Updates."
                    : string.Empty;
                UpdateStatusText = available
                    ? $"v{version} is available — you have v{AppVersion.Display}."
                    : $"{status} · checked {DateTime.Now:HH:mm}";
                OnPropertyChanged(nameof(UpdateButtonText));
            });
        }
    }

    /// <summary>Download → verify → swap the exe in place → start the new version (which closes this one).</summary>
    private async Task InstallUpdateAsync(CancellationToken token)
    {
        if (!IsUpdateAvailable || IsInstallingUpdate) return;
        if (!SelfUpdateSupported || string.IsNullOrEmpty(_updateService.DownloadUrl))
        {
            OpenUpdatePage();
            return;
        }

        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
        {
            UpdateStatusText = "Cannot locate the running program file — opening the download page instead.";
            OpenUpdatePage();
            return;
        }

        IsInstallingUpdate = true;
        UpdateProgress = 0;
        string version = _updateService.LatestVersion ?? "";
        try
        {
            UpdateStatusText = $"Downloading v{version}…";
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p * 100;
                UpdateStatusText = $"Downloading v{version}… {p:P0}";
            });
            string? file = await _updateService.DownloadUpdateAsync(SelfUpdater.StagingDirectory, progress, token);
            if (file is null)
            {
                UpdateStatusText = _updateService.Status + " — nothing was changed.";
                return;
            }

            UpdateStatusText = "Verifying the download…";
            string? expected = await _updateService.GetExpectedSha256Async(token);
            if (expected is null)
            {
                TryDeleteFile(file);
                UpdateStatusText = "The release has no checksum for this file, so it was not installed. Use the download page.";
                AppLog.Warn("Update: SHA256SUMS.txt missing or does not list the asset — update refused.");
                return;
            }
            string actual = await SelfUpdater.ComputeSha256Async(file, token);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(file);
                UpdateStatusText = "The download does not match the published checksum — it was deleted and nothing was changed.";
                AppLog.Warn($"Update: checksum mismatch (expected {expected}, got {actual}).");
                return;
            }

            UpdateStatusText = $"Installing v{version}…";
            await SaveSettingsAsync();
            try
            {
                await Task.Run(() => SelfUpdater.Swap(currentExe, file), token);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Update: replacing the program file failed", ex);
                UpdateStatusText = $"Could not replace the program file ({ex.Message}). Nothing was changed — use the download page.";
                return;
            }

            AppLog.Info($"Update: v{AppVersion.Display} → v{version} installed at {currentExe}; starting it.");
            UpdateStatusText = $"v{version} installed — restarting FANZi…";
            SelfUpdater.StartNewVersion(currentExe); // newer build: asks this instance to exit (fans back to automatic)
        }
        catch (OperationCanceledException) { UpdateStatusText = "Update cancelled — nothing was changed."; }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private void OpenUpdatePage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _updateService.ReleasePageUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Opening the release page failed", ex);
            StatusMessage = $"Open {_updateService.ReleasePageUrl} in your browser to update.";
        }
    }
}
