using Avalonia;
using Fanzi.FanControl.Services;
using System;
using System.Linq;
using System.Threading;

namespace Fanzi.FanControl;

sealed class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        CrashGuardService.Initialize();

        _singleInstanceMutex = new Mutex(true, "Global\\FANZI_SingleInstance", out bool createdNew);
        if (!createdNew)
            return;

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
