using Avalonia;
using Fanzi.FanControl.Services;
using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;

namespace Fanzi.FanControl;

[SupportedOSPlatform("windows")]
sealed class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        CrashGuardService.Initialize();

        _singleInstanceMutex = new Mutex(true, "Global\\FANZI_IONITY_SingleInstance_v3", out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running — bring it to foreground if possible
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("Fanzi.FanControl");
                foreach (var p in procs)
                {
                    if (p.Id != System.Diagnostics.Process.GetCurrentProcess().Id)
                    {
                        // Signal the other instance (it will show its window)
                        break;
                    }
                }
            }
            catch { }
            return;
        }

        try
        {
            bool startMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
            App.StartMinimizedFromArgs = startMinimized;

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashGuardService.WriteCrashLog(ex, "Main", isTerminating: true);
            throw;
        }
        finally
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
