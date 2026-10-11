namespace Fanzi.FanControl.Models;

using System;
using System.Collections.Generic;

/// <summary>Device families shown with their own icon and wording in the RGB tab (v4).</summary>
public enum RgbDeviceCategory
{
    Other, Motherboard, Ram, Gpu, Cooler, LedStrip, Keyboard, Mouse, MouseMat, Headset, HeadsetStand,
    Gamepad, Light, Speaker, Virtual, Storage, Case, Microphone, Accessory, Keypad, Laptop, Monitor,
}

/// <summary>Represents a connected OpenRGB device and its zone LEDs.</summary>
public sealed record RgbDeviceInfo(
    int    DeviceIndex,
    string Name,
    string Type,
    int    LedCount,
    bool   IsConnected)
{
    /// <summary>Addressable zones within this device (never null).</summary>
    public IReadOnlyList<RgbZoneInfo> Zones { get; init; } = Array.Empty<RgbZoneInfo>();

    /// <summary>How FANZi drives this device, e.g. "Per-LED · Direct" or "Single colour · Static".</summary>
    public string ControlLabel { get; init; } = string.Empty;

    /// <summary>False for effects-only devices whose colour cannot be set.</summary>
    public bool IsControllable { get; init; } = true;

    /// <summary>Vendor reported by OpenRGB (e.g. "ASUS" for ENE DRAM controllers).</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>Bus location reported by OpenRGB (I2C address, HID path …) — used by the deep scan.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>Family derived from OpenRGB's device type (numeric for types newer than the SDK enum).</summary>
    public RgbDeviceCategory Category => CategoryFor(Type);

    /// <summary>"RAM", "GPU", "Monitor", … for the device card.</summary>
    public string FriendlyType => Category switch
    {
        RgbDeviceCategory.Ram => "RAM",
        RgbDeviceCategory.Gpu => "GPU",
        RgbDeviceCategory.LedStrip => "LED strip / ARGB",
        RgbDeviceCategory.Cooler => "Cooler / fans",
        RgbDeviceCategory.MouseMat => "Mouse mat",
        RgbDeviceCategory.HeadsetStand => "Headset stand",
        RgbDeviceCategory.Other => string.IsNullOrWhiteSpace(Type) ? "Device" : Type,
        _ => Category.ToString(),
    };

    /// <summary>Glyph for the device card (font-safe, no colour emoji).</summary>
    public string Icon => Category switch
    {
        RgbDeviceCategory.Ram => "▤",
        RgbDeviceCategory.Gpu => "▣",
        RgbDeviceCategory.Motherboard => "▦",
        RgbDeviceCategory.Cooler => "✇",
        RgbDeviceCategory.LedStrip => "┅",
        RgbDeviceCategory.Keyboard or RgbDeviceCategory.Keypad => "⌨",
        RgbDeviceCategory.Mouse => "◉",
        RgbDeviceCategory.Monitor or RgbDeviceCategory.Laptop => "▭",
        RgbDeviceCategory.Headset or RgbDeviceCategory.HeadsetStand => "◠",
        RgbDeviceCategory.Speaker => "◍",
        _ => "●",
    };

    /// <summary>Name for the UI; multiple identical RAM sticks get a slot number (see <see cref="Slot"/>).</summary>
    public string DisplayName => Slot is int n ? $"{Name} · stick {n}" : Name;

    /// <summary>1-based index among devices with the same name (set by the view model), null when unique.</summary>
    public int? Slot { get; init; }

    /// <summary>Up to 24 dots for the card's LED preview strip.</summary>
    public int PreviewDotCount => Math.Clamp(LedCount, 1, 24);

    public static RgbDeviceCategory CategoryFor(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return RgbDeviceCategory.Other;
        if (int.TryParse(type, out int n))
            return n is >= 0 and <= 20 ? (RgbDeviceCategory)(n + 1) : RgbDeviceCategory.Other;

        return type.Trim().ToLowerInvariant() switch
        {
            "motherboard" => RgbDeviceCategory.Motherboard,
            "dram" or "ram" => RgbDeviceCategory.Ram,
            "gpu" => RgbDeviceCategory.Gpu,
            "cooler" => RgbDeviceCategory.Cooler,
            "ledstrip" => RgbDeviceCategory.LedStrip,
            "keyboard" => RgbDeviceCategory.Keyboard,
            "mouse" => RgbDeviceCategory.Mouse,
            "mousemat" => RgbDeviceCategory.MouseMat,
            "headset" => RgbDeviceCategory.Headset,
            "headsetstand" => RgbDeviceCategory.HeadsetStand,
            "gamepad" => RgbDeviceCategory.Gamepad,
            "light" => RgbDeviceCategory.Light,
            "speaker" => RgbDeviceCategory.Speaker,
            "virtual" => RgbDeviceCategory.Virtual,
            "storage" => RgbDeviceCategory.Storage,
            "case" => RgbDeviceCategory.Case,
            "microphone" => RgbDeviceCategory.Microphone,
            "accessory" => RgbDeviceCategory.Accessory,
            "keypad" => RgbDeviceCategory.Keypad,
            "laptop" => RgbDeviceCategory.Laptop,
            "monitor" => RgbDeviceCategory.Monitor,
            _ => RgbDeviceCategory.Other,
        };
    }
}
