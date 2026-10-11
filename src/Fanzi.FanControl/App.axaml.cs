using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;
using Fanzi.FanControl.Views;
using System;
using System.Linq;
using System.Runtime.Versioning;

namespace Fanzi.FanControl;

[SupportedOSPlatform("windows")]
public partial class App : Application
{
    private IHardwareMonitorService? _hardwareMonitorService;
    private IRgbService? _rgbService;
    private ISettingsService? _settingsService;

    public static bool StartMinimizedFromArgs { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            // Windows: LibreHardwareMonitor (guards its own driver start-up via InitializationError);
            // Linux / macOS: hwmon / SMC readers (4.1) — see HardwareMonitorFactory.
            _hardwareMonitorService = HardwareMonitorFactory.Create();
            _rgbService = new OpenRgbService();
            _settingsService = new SettingsService();
            var viewModel = new MainWindowViewModel(_hardwareMonitorService, _rgbService, _settingsService);

            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            DataContext = viewModel;

            bool shuttingDown = false;

            mainWindow.Closing += (_, e) =>
            {
                if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
                {
                    shuttingDown = true;
                    return;
                }

                if (viewModel.CloseToTray)
                {
                    e.Cancel = true;
                    mainWindow.Hide();
                }
            };

            // Close-to-tray off: closing the window ends the app (ShutdownMode is explicit).
            mainWindow.Closed += (_, _) =>
            {
                if (shuttingDown) return;
                shuttingDown = true;
                try { desktop.Shutdown(); }
                catch (Exception ex) { AppLog.Warn("Shutdown after main window close failed", ex); }
            };

            desktop.Exit += (_, _) =>
            {
                AppLog.Info("Application exit: restoring automatic fan control and releasing hardware.");
                try { viewModel.RestoreAutomaticControlOnExit(); }
                catch (Exception ex) { AppLog.Warn("Restoring automatic fan control on exit failed", ex); }

                try { viewModel.Dispose(); } catch (Exception ex) { AppLog.Warn("View model dispose failed", ex); }
                try { _hardwareMonitorService.Dispose(); } catch (Exception ex) { AppLog.Warn("Hardware monitor dispose failed", ex); }
                try { _rgbService.Dispose(); } catch (Exception ex) { AppLog.Warn("RGB service dispose failed", ex); }
            };

            if (StartMinimizedFromArgs)
            {
                MinimizeMainWindow(mainWindow, viewModel);
            }
            else
            {
                // StartMinimized lives in settings, which load asynchronously — apply it once they're in.
                viewModel.SettingsLoaded += (_, _) =>
                {
                    if (viewModel.StartMinimized)
                        MinimizeMainWindow(mainWindow, viewModel);
                };
            }

            // v4: a newer FANZi build asks us to make way — shut down cleanly (fans restored in desktop.Exit).
            UpgradeHandover.StartExitListener(() =>
                Dispatcher.UIThread.Post(() =>
                {
                    try { desktop.Shutdown(); }
                    catch (Exception ex) { AppLog.Warn("Upgrade shutdown failed", ex); }
                }));

            // A second launch signals us: bring the window back.
            Program.StartShowWindowListener(() =>
                Dispatcher.UIThread.Post(() => viewModel.ShowWindowCommand.Execute(null)));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void MinimizeMainWindow(Window window, MainWindowViewModel viewModel)
    {
        try
        {
            window.WindowState = WindowState.Minimized;
            if (viewModel.MinimizeToTray)
                window.Hide();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Start minimized failed", ex);
        }
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled exception on the UI thread (kept running)", e.Exception);
        CrashGuardService.WriteCrashLog(e.Exception, "DispatcherUnhandledException", isTerminating: false);
        e.Handled = true;
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
