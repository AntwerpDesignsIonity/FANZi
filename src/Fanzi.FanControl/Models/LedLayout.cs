using System;
using System.Collections.Generic;

namespace Fanzi.FanControl.Models;

/// <summary>Where a LED sits on its device, in normalised 0..1 coordinates.</summary>
public readonly record struct LedPoint(double X, double Y);

/// <summary>
/// LED geometry of one device as Typing FX sees it: a keyboard matrix becomes real 2-D positions; a strip, RAM stick
/// or GPU becomes a line along X. Built once per device from OpenRGB's zone data (see <see cref="FromMatrix"/>,
/// <see cref="Linear"/>).
/// </summary>
public sealed class LedLayout
{
    public IReadOnlyList<LedPoint> Points { get; }
    public bool IsMatrix { get; }
    public int Columns { get; }
    public int Rows { get; }

    private LedLayout(IReadOnlyList<LedPoint> points, bool isMatrix, int columns, int rows)
    {
        Points = points; IsMatrix = isMatrix; Columns = columns; Rows = rows;
    }

    /// <summary>LEDs evenly spaced along X (RAM sticks, strips, fans, GPUs).</summary>
    public static LedLayout Linear(int ledCount)
    {
        ledCount = Math.Max(ledCount, 1);
        var pts = new LedPoint[ledCount];
        for (int i = 0; i < ledCount; i++)
            pts[i] = new LedPoint(ledCount == 1 ? 0.5 : (double)i / (ledCount - 1), 0.5);
        return new LedLayout(pts, false, ledCount, 1);
    }

    /// <summary>
    /// From an OpenRGB matrix map: <paramref name="cell"/>(row, col) returns the LED index at that cell or -1 for a
    /// gap. LEDs absent from the map are placed along the bottom edge so every LED has a position.
    /// </summary>
    public static LedLayout FromMatrix(int ledCount, int width, int height, Func<int, int, int> cell)
    {
        var pts = new LedPoint[Math.Max(ledCount, 1)];
        var seen = new bool[pts.Length];
        for (int r = 0; r < height; r++)
        for (int c = 0; c < width; c++)
        {
            int idx = cell(r, c);
            if (idx < 0 || idx >= pts.Length) continue;
            pts[idx] = new LedPoint(width == 1 ? 0.5 : (double)c / (width - 1), height == 1 ? 0.5 : (double)r / (height - 1));
            seen[idx] = true;
        }
        int unplaced = 0;
        for (int i = 0; i < pts.Length; i++)
            if (!seen[i]) pts[i] = new LedPoint((unplaced++ % 16) / 15.0, 1.0);
        return new LedLayout(pts, true, Math.Max(width, 1), Math.Max(height, 1));
    }
}
