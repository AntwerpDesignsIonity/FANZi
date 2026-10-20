using System;
using System.Collections.Generic;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>
/// Where each Windows virtual-key sits on a standard ANSI tenkeyless board, as normalised (0..1) coordinates —
/// so Typing FX can start a ripple at the right spot even on a keyboard OpenRGB only exposes as a strip of LEDs.
/// Rows: 0 = function row … 5 = space row. Columns are approximate key centres in units of 1/18 board width
/// (letters + the nav cluster = 18 key widths on a TKL; full-size boards add the numpad on the right).
/// </summary>
public static class KeyboardGeometry
{
    private static readonly Dictionary<int, (double Col, int Row)> Map = Build();

    /// <summary>Normalised position for a virtual-key code, or null when the key isn't on the map.</summary>
    public static LedPoint? Position(int vk)
    {
        if (!Map.TryGetValue(vk, out var p)) return null;
        return new LedPoint(Math.Clamp(p.Col / 17.5, 0, 1), Math.Clamp(p.Row / 5.0, 0, 1));
    }

    /// <summary>Keys that act as a "line break" for Typewriter FX (Enter, Space, Tab).</summary>
    public static bool IsBreakKey(int vk) => vk is 0x0D or 0x20 or 0x09;

    /// <summary>OpenRGB LED name ("Key: A", "Key: Left Shift" …) for a virtual key, when OpenRGB uses that label.</summary>
    public static string? OpenRgbLedName(int vk)
    {
        if (vk >= 'A' && vk <= 'Z') return "Key: " + (char)vk;
        if (vk >= '0' && vk <= '9') return "Key: " + (char)vk;
        if (vk >= 0x70 && vk <= 0x7B) return "Key: F" + (vk - 0x70 + 1);
        return vk switch
        {
            0x1B => "Key: Escape", 0x09 => "Key: Tab", 0x14 => "Key: Caps Lock", 0xA0 => "Key: Left Shift",
            0xA1 => "Key: Right Shift", 0xA2 => "Key: Left Control", 0xA3 => "Key: Right Control",
            0xA4 => "Key: Left Alt", 0xA5 => "Key: Right Alt", 0x5B => "Key: Left Windows", 0x5D => "Key: Menu",
            0x20 => "Key: Space", 0x0D => "Key: Enter", 0x08 => "Key: Backspace", 0x2D => "Key: Insert",
            0x2E => "Key: Delete", 0x24 => "Key: Home", 0x23 => "Key: End", 0x21 => "Key: Page Up", 0x22 => "Key: Page Down",
            0x25 => "Key: Left Arrow", 0x26 => "Key: Up Arrow", 0x27 => "Key: Right Arrow", 0x28 => "Key: Down Arrow",
            0x2C => "Key: Print Screen", 0x91 => "Key: Scroll Lock", 0x13 => "Key: Pause/Break",
            0xC0 => "Key: `", 0xBD => "Key: -", 0xBB => "Key: =", 0xDB => "Key: [", 0xDD => "Key: ]", 0xDC => "Key: \\",
            0xBA => "Key: ;", 0xDE => "Key: '", 0xBC => "Key: ,", 0xBE => "Key: .", 0xBF => "Key: /",
            _ => null,
        };
    }

    private static Dictionary<int, (double, int)> Build()
    {
        var m = new Dictionary<int, (double, int)>();
        void Row(int row, double startCol, double step, params int[] vks)
        {
            double c = startCol;
            foreach (int vk in vks) { m[vk] = (c, row); c += step; }
        }

        // Function row: Esc, F1–F4, F5–F8, F9–F12, PrtSc/ScrLk/Pause
        m[0x1B] = (0, 0);
        Row(0, 2, 1, 0x70, 0x71, 0x72, 0x73);
        Row(0, 6.5, 1, 0x74, 0x75, 0x76, 0x77);
        Row(0, 11, 1, 0x78, 0x79, 0x7A, 0x7B);
        Row(0, 15.25, 1, 0x2C, 0x91, 0x13);

        // Number row + Ins/Home/PgUp
        Row(1, 0, 1, 0xC0, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', 0xBD, 0xBB);
        m[0x08] = (13.5, 1);
        Row(1, 15.25, 1, 0x2D, 0x24, 0x21);

        // QWERTY row + Del/End/PgDn
        m[0x09] = (0.5, 2);
        Row(2, 1.5, 1, 'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P', 0xDB, 0xDD);
        m[0xDC] = (13.75, 2);
        Row(2, 15.25, 1, 0x2E, 0x23, 0x22);

        // Home row
        m[0x14] = (0.75, 3);
        Row(3, 1.75, 1, 'A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', 0xBA, 0xDE);
        m[0x0D] = (13.5, 3);

        // Shift row + Up arrow
        m[0xA0] = (1.0, 4);
        Row(4, 2.25, 1, 'Z', 'X', 'C', 'V', 'B', 'N', 'M', 0xBC, 0xBE, 0xBF);
        m[0xA1] = (13.25, 4);
        m[0x26] = (16.25, 4);

        // Bottom row + arrows
        Row(5, 0.5, 1.25, 0xA2, 0x5B, 0xA4);
        m[0x20] = (7, 5);
        Row(5, 10.75, 1.25, 0xA5, 0x5C, 0x5D, 0xA3);
        Row(5, 15.25, 1, 0x25, 0x28, 0x27);

        // Numpad (full-size boards) — placed to the right of the nav cluster
        Row(1, 18.5, 1, 0x90, 0x6F, 0x6A, 0x6D);
        Row(2, 18.5, 1, 0x67, 0x68, 0x69);
        Row(3, 18.5, 1, 0x64, 0x65, 0x66);
        Row(4, 18.5, 1, 0x61, 0x62, 0x63);
        m[0x6B] = (21.5, 2);
        m[0x60] = (19, 5);
        m[0x6E] = (20.5, 5);
        return m;
    }
}
