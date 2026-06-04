namespace Fanzi.FanControl.Models;

/// <summary>
/// Which live hardware signal drives a reactive RGB effect. Lets each zone react to a
/// different sensor — e.g. RAM follows CPU temperature while the GPU block follows GPU
/// temperature and a fan ring follows the AIO pump speed.
/// </summary>
public enum RgbReactiveSource
{
    /// <summary>No hardware input — the effect uses its own animation only.</summary>
    None,

    /// <summary>CPU package temperature.</summary>
    CpuTemperature,

    /// <summary>GPU core temperature.</summary>
    GpuTemperature,

    /// <summary>CPU total load percentage.</summary>
    CpuLoad,

    /// <summary>AIO pump / liquid-cooler speed (RPM), normalised to 0-100.</summary>
    PumpSpeed,
}
