using Avalonia;
using Fanzi.FanControl.Services;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Fanzi.FanControl;

[SupportedOSPlatform("windows")]
sealed class Program
{
    private const string SingleInstanceMutexName = "Global\\FANZI_IONITY_SingleInstance_v3";

    /// <summary>A second instance sets this event so the running instance shows its window.</summary>
    internal const string ShowWindowEventName = "FANZI_IONITY_ShowWindow";

    private static readonly TimeSpan RecoveredMutexWait = TimeSpan.FromSeconds(10);

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showWindowEvent;

    [STAThread]
    public static void Main(string[] args)
    {
        bool recovered = CrashGuardService.IsRecoveredStart(args);
        int restartCount = CrashGuardService.ParseRestartCount(args);
        bool startMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        CrashGuardService.Initialize(restartCount, startMinimized);

        AppLog.Info($"FANZi IO-nity v{AppVersion.Display} starting (pid {Environment.ProcessId}, args: {string.Join(' ', args)}, " +
                    $"OS: {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, {RuntimeInformation.FrameworkDescription})");

        bool ownsMutex = AcquireSingleInstance(recovered);

        // v4: a newer build replaces an older FANZi that is still running (e.g. v3 sitting in the tray),
        // instead of just bringing the old window to the front — "I started v4 but v3 opened".
        if (!ownsMutex && UpgradeHandover.ReplaceOlderInstances(AppVersion.Display))
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            ownsMutex = AcquireSingleInstance(recovered: true); // waits up to 10 s; an abandoned lock passes to us
        }

        if (!ownsMutex)
        {
            AppLog.Info("Another FANZi instance is already running — asking it to show its window, then exiting.");
            SignalRunningInstance();
            _singleInstanceMutex?.Dispose();
            return;
        }

        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not create the show-window event; a second launch won't restore this window.", ex);
        }

        try
        {
            App.StartMinimizedFromArgs = startMinimized;

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Error("Fatal exception in Main", ex);
            CrashGuardService.WriteCrashLog(ex, "Main", isTerminating: true);
            throw;
        }
        finally
        {
            AppLog.Info("FANZi IO-nity exiting.");
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _singleInstanceMutex?.Dispose();
        }
    }

    /// <summary>
    /// Takes the single-instance mutex. A CrashGuard restart (<c>--recovered</c>) starts while the
    /// crashed process may still hold it, so it waits up to 10 s instead of giving up immediately.
    /// </summary>
    private static bool AcquireSingleInstance(bool recovered)
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            if (createdNew)
                return true;

            if (!recovered)
                return false;

            AppLog.Info("Recovered start: waiting for the previous instance to release the single-instance lock...");
            try
            {
                return _singleInstanceMutex.WaitOne(RecoveredMutexWait);
            }
            catch (AbandonedMutexException)
            {
                // The crashed process died holding it — ownership passes to us.
                return true;
            }
        }
        catch (Exception ex)
        {
            // If the mutex itself is unusable, run rather than refuse to start.
            AppLog.Warn("Single-instance mutex unavailable — continuing without it.", ex);
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            return true;
        }
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out EventWaitHandle? showEvent))
            {
                using (showEvent)
                    showEvent.Set();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not signal the running FANZi instance.", ex);
        }
    }

    /// <summary>Waits on a background thread for second-instance launches and runs <paramref name="onSignal"/>.</summary>
    internal static void StartShowWindowListener(Action onSignal)
    {
        EventWaitHandle? handle = _showWindowEvent;
        if (handle is null)
            return;

        var thread = new Thread(() =>
        {
            try
            {
                while (handle.WaitOne())
                {
                    AppLog.Info("Second launch detected — showing the main window.");
                    try { onSignal(); }
                    catch (Exception ex) { AppLog.Warn("Show-window request failed", ex); }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Warn("Show-window listener stopped", ex);
            }
        })
        {
            IsBackground = true,
            Name = "FANZI show-window listener",
        };
        thread.Start();
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // 4.1: the running ThermalFrame line redraws only the thin window-border strips every frame;
            // region dirty rects keep that from repainting the whole dashboard behind it.
            .With(new Avalonia.Rendering.Composition.CompositionOptions { UseRegionDirtyRectClipping = true })
            .LogToTrace();
}
