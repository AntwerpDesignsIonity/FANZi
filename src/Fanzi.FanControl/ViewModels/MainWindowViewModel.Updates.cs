using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// Background release check: shows a title-bar badge when a newer build is published.
/// The badge opens the release page; installing stays with the companion launcher, which
/// replaces the exe safely while FANZi is not running.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);

    private readonly AutoUpdateService _updateService = new();

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateBadgeText = string.Empty;
    [ObservableProperty] private string _updateTooltip = string.Empty;

    private IRelayCommand? _openUpdatePageCommand;
    public IRelayCommand OpenUpdatePageCommand => _openUpdatePageCommand ??= new RelayCommand(OpenUpdatePage);

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
            bool available = await _updateService.CheckForUpdateAsync(token).ConfigureAwait(false);
            AppLog.Info($"Update check: {_updateService.Status}");

            string version = _updateService.LatestVersion ?? "";
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                IsUpdateAvailable = available;
                UpdateBadgeText = available ? $"Update v{version}" : string.Empty;
                UpdateTooltip = available
                    ? $"FANZi IO-nity v{version} is available (you have v{AppVersion.Display}). Click to open the release page."
                    : string.Empty;
            });

            await Task.Delay(UpdateCheckInterval, token).ConfigureAwait(false);
        }
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
