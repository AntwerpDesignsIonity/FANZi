using Avalonia;
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
        _singleInstanceMutex = new Mutex(true, "Global\\FANZI_SingleInstance", out bool createdNew);
        if (!createdNew)
            return;

        try
        {
            bool startMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
            App.StartMinimizedFromArgs = startMinimized;

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
