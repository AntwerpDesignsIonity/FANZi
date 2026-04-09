using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;
using Fanzi.FanControl.Views;

namespace Fanzi.FanControl;

public partial class App : Application
{
    private IHardwareMonitorService? _hardwareMonitorService;
    private IRgbService?             _rgbService;
    private ISettingsService?        _settingsService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            _hardwareMonitorService = new HardwareMonitorService();
            _rgbService             = new OpenRgbService();
            _settingsService        = new SettingsService();
            var viewModel = new MainWindowViewModel(_hardwareMonitorService, _rgbService, _settingsService);

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                _hardwareMonitorService.Dispose();
                _rgbService.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}