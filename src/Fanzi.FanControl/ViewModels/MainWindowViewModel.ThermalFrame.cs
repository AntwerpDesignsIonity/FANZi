using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// FANZi 4.1 thermal frame: the running temperature line around the main window and its title-bar chip.
/// The frame follows the hotter of the CPU gauge temperature (package → hotspot → average core) and the GPU core.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Show the running temperature line (persisted as <see cref="AppSettings.ThermalFrameEnabled"/>).</summary>
    [ObservableProperty] private bool _thermalFrameEnabled = true;

    /// <summary>Temperature the frame shows: max(CPU gauge, GPU core); null when neither sensor reports.</summary>
    public double? FrameTemperatureValue => (CpuGaugeTempValue, GpuCoreTempValue) switch
    {
        (double cpu, double gpu) => System.Math.Max(cpu, gpu),
        (double cpu, null) => cpu,
        (null, double gpu) => gpu,
        _ => null,
    };

    /// <summary>Thermal state of <see cref="FrameTemperatureValue"/> (cold / cool / stable / warm / hot / critical).</summary>
    public ThermalState FrameThermalState => ThermalFrameMath.StateFor(FrameTemperatureValue);

    /// <summary>Title-bar chip text, e.g. "64 °C · STABLE".</summary>
    public string FrameTemperatureText => FrameTemperatureValue is double t
        ? $"{t:F0} °C · {ThermalFrameMath.StateLabel(FrameThermalState)}"
        : ThermalFrameMath.StateLabel(ThermalState.Unknown);

    /// <summary>Tooltip of the chip: which sensor drives the frame and what the colours mean.</summary>
    public string FrameTemperatureTooltip => FrameTemperatureValue is double t
        ? $"Thermal frame: {t:F1} °C (hotter of CPU and GPU) — {ThermalFrameMath.StateLabel(FrameThermalState)}.\n" +
          "Blue = cold · green = stable · amber / orange = warm · red = hot. Turn it off in Settings → Appearance."
        : "Thermal frame: no CPU or GPU temperature sensor yet.";

    partial void OnCpuGaugeTempValueChanged(double? value) => NotifyFrameTemperature();

    partial void OnGpuCoreTempValueChanged(double? value) => NotifyFrameTemperature();

    private void NotifyFrameTemperature()
    {
        OnPropertyChanged(nameof(FrameTemperatureValue));
        OnPropertyChanged(nameof(FrameThermalState));
        OnPropertyChanged(nameof(FrameTemperatureText));
        OnPropertyChanged(nameof(FrameTemperatureTooltip));
    }

    partial void OnThermalFrameEnabledChanged(bool value)
    {
        if (_suppressProfileSync) return;
        _appSettings.ThermalFrameEnabled = value;
        _ = SaveSettingsAsync();
    }
}
