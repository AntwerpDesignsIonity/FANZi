using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Companions;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// v4.5.0 "Ionity apps" card on the Tools tab: WISP (Aura Wisp) as an additional application. One Launch
/// button, one Download link, and an unseen monitor - a 10 s timer with no UI of its own - that notices
/// when WISP starts, stops or crashes, and (when "Keep WISP running" is on) quietly brings it back.
/// </summary>
public sealed partial class ToolsViewModel
{
    private const int CompanionPollMs = 10_000;
    private static readonly string KeepFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FANZI", "wisp-keep.flag");

    private DispatcherTimer? _companionTimer;
    private bool _wispWasRunning;
    private DateTime _lastWispRelaunch = DateTime.MinValue;
    private bool _companionInit;

    public string WispName => WispCompanionService.DisplayName;
    public string WispBlurb => "A living light that follows your focus - a glowing, colour-changeable aura around the active window, " +
                               "a screensaver across every screen, and your case RGB mirrored through FANZi's OpenRGB server.";

    [ObservableProperty] private bool _wispInstalled;
    [ObservableProperty] private bool _wispRunning;
    [ObservableProperty] private string _wispStatus = "Checking…";
    [ObservableProperty] private string _wispVersion = "";
    [ObservableProperty] private bool _keepWispRunning;

    public bool WispCanLaunch => WispInstalled && !WispRunning;
    public bool WispNotInstalled => !WispInstalled;

    partial void OnWispInstalledChanged(bool value) { OnPropertyChanged(nameof(WispCanLaunch)); OnPropertyChanged(nameof(WispNotInstalled)); }
    partial void OnWispRunningChanged(bool value) => OnPropertyChanged(nameof(WispCanLaunch));

    partial void OnKeepWispRunningChanged(bool value)
    {
        if (!_companionInit) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeepFile)!);
            if (value) File.WriteAllText(KeepFile, DateTime.Now.ToString("s"));
            else if (File.Exists(KeepFile)) File.Delete(KeepFile);
        }
        catch (Exception ex) { AppLog.Warn("Could not save the WISP keep-running flag", ex); }
        if (value && WispInstalled && !WispRunning) _ = LaunchWispAsync();
    }

    /// <summary>Starts the unseen monitor. Safe to call more than once.</summary>
    internal void EnsureCompanionMonitor()
    {
        if (_companionTimer is not null) return;
        try { KeepWispRunning = File.Exists(KeepFile); } catch { }
        _companionInit = true;
        RefreshWisp(first: true);
        _companionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(CompanionPollMs), DispatcherPriority.Background, (_, _) => RefreshWisp(first: false));
        _companionTimer.Start();
    }

    private void RefreshWisp(bool first)
    {
        try
        {
            var exe = WispCompanionService.Locate();
            bool running = WispCompanionService.IsRunning();
            WispInstalled = exe is not null;
            WispRunning = running;
            if (first || string.IsNullOrEmpty(WispVersion))
                WispVersion = WispCompanionService.Version(exe) is { } v ? "v" + v : "";

            if (!first && running != _wispWasRunning)
                AppLog.Info(running ? "WISP started." : "WISP is no longer running.");

            if (!running && (_wispWasRunning || first) && KeepWispRunning && exe is not null
                && DateTime.UtcNow - _lastWispRelaunch > TimeSpan.FromSeconds(45))
            {
                _lastWispRelaunch = DateTime.UtcNow;
                if (WispCompanionService.Launch(out var msg)) { AppLog.Info("WISP relaunched by FANZi: " + msg); running = true; WispRunning = true; }
            }
            _wispWasRunning = running;

            WispStatus = exe is null
                ? "Not installed - get it from ionity.digital"
                : running ? "Running" + (KeepWispRunning ? " · watched" : "")
                : "Installed, not running" + (KeepWispRunning ? " · will start" : "");
        }
        catch (Exception ex)
        {
            AppLog.Warn("WISP companion check failed", ex);
            WispStatus = "Could not check WISP";
        }
    }

    [RelayCommand]
    private Task LaunchWispAsync()
    {
        return Task.Run(() =>
        {
            bool ok = WispCompanionService.Launch(out var msg);
            Dispatcher.UIThread.Post(() =>
            {
                Log((ok ? "✔ " : "✖ ") + msg);
                LastResultOk = ok;
                LastResult = msg;
                RefreshWisp(first: false);
            });
        });
    }

    [RelayCommand]
    private void StopWisp()
    {
        bool any = WispCompanionService.Stop();
        Log(any ? "⏹ WISP closed." : "WISP was not running.");
        _wispWasRunning = false;
        RefreshWisp(first: false);
    }

    [RelayCommand]
    private void OpenWispDownload()
    {
        try { Process.Start(new ProcessStartInfo(WispCompanionService.DownloadUrl) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn("Could not open the WISP download page", ex); }
    }

    /// <summary>For a portable WISP build: point FANZi at its exe once; it is remembered.</summary>
    [RelayCommand]
    private async Task LocateWisp(Control? anchor)
    {
        try
        {
            var top = anchor is null ? null : TopLevel.GetTopLevel(anchor);
            if (top is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Find WISP.exe (or the portable WISP-Portable-*.exe)",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("WISP") { Patterns = new[] { "WISP*.exe" } }, FilePickerFileTypes.All }
            });
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(path)) return;
            WispCompanionService.PinPath(path);
            WispVersion = "";
            RefreshWisp(first: true);
            Log("WISP found at " + path);
        }
        catch (Exception ex) { AppLog.Warn("Locate WISP failed", ex); }
    }

    private void DisposeCompanions()
    {
        _companionTimer?.Stop();
        _companionTimer = null;
    }
}
