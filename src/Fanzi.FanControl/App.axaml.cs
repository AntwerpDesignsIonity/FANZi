using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            _hardwareMonitorService = new HardwareMonitorService();
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

            mainWindow.Closing += (_, e) =>
            {
                if (viewModel.CloseToTray)
                {
                    e.Cancel = true;
                    mainWindow.Hide();
                }
            };

            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                _hardwareMonitorService.Dispose();
                _rgbService.Dispose();
            };

            if (StartMinimizedFromArgs || viewModel.StartMinimized)
            {
                mainWindow.WindowState = WindowState.Minimized;
                if (viewModel.MinimizeToTray)
                    mainWindow.Hide();
            }
        }

        base.OnFrameworkInitializationCompleted();
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
