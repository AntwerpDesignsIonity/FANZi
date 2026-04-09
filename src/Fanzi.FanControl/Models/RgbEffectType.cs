namespace Fanzi.FanControl.Models;

/// <summary>Built-in RGB lighting effect types.</summary>
public enum RgbEffectType
{
    /// <summary>Solid static colour, no animation.</summary>
    Static,

    /// <summary>Breathing / pulse: fades in and out.</summary>
    Pulse,

    /// <summary>Full-spectrum HSV hue cycle.</summary>
    Rainbow,

    /// <summary>Colour wave that rolls across all devices.</summary>
    ColorWave,

    /// <summary>
    /// Colour driven by the highest CPU or GPU temperature.
    /// Cool blue at idle → red at thermal limit.
    /// </summary>
    TemperatureReactive,

    /// <summary>
    /// Speed and brightness driven by CPU total load.
    /// Slow / dim at idle → rapid / bright under load.
    /// </summary>
    CpuLoadReactive,

    /// <summary>
    /// Combines CPU load (speed) with temperature (colour).
    /// The full "Performance" experience.
    /// </summary>
    Performance,

    /// <summary>Rapid strobe / flash.</summary>
    Strobe,

    /// <summary>Two-colour alternating flash at a user-selected rate.</summary>
    DualColorFlash,
}
