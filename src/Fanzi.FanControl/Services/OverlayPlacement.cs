using Avalonia;
using System;
using System.Collections.Generic;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Pure placement maths for the mini overlay: restores a saved position clamped onto a screen's
/// working area, or falls back to the top-right corner of the primary screen.
/// </summary>
internal static class OverlayPlacement
{
    /// <summary>Gap (pixels) between the default position and the working-area edges.</summary>
    internal const int DefaultMargin = 16;

    /// <summary>
    /// Returns where the overlay should open.
    /// <list type="bullet">
    /// <item>No saved position, or no working areas known → top-right of <paramref name="primaryWorkingArea"/>.</item>
    /// <item>Saved position overlapping a working area → clamped fully inside the area it overlaps most.</item>
    /// <item>Saved position on no screen (monitor unplugged) → default position.</item>
    /// </list>
    /// </summary>
    internal static PixelPoint Resolve(
        int? savedX,
        int? savedY,
        PixelSize windowSize,
        IReadOnlyList<PixelRect> workingAreas,
        PixelRect primaryWorkingArea)
    {
        // Same-DPI convenience overload: every screen sees the same physical size.
        var screens = new List<(PixelRect WorkingArea, double Scaling)>(workingAreas.Count);
        foreach (var area in workingAreas) screens.Add((area, 1.0));

        return Resolve(savedX, savedY, new Size(windowSize.Width, windowSize.Height), screens, primaryWorkingArea, 1.0);
    }

    /// <summary>
    /// Mixed-DPI aware variant: <paramref name="logicalSize"/> is converted to physical pixels with the
    /// scaling of each candidate screen, so a window on a 150 % monitor is clamped with its real size.
    /// The screen containing the saved top-left point wins; otherwise the one the window overlaps most.
    /// </summary>
    internal static PixelPoint Resolve(
        int? savedX,
        int? savedY,
        Size logicalSize,
        IReadOnlyList<(PixelRect WorkingArea, double Scaling)> screens,
        PixelRect primaryWorkingArea,
        double primaryScaling)
    {
        var primarySize = PixelSize.FromSize(logicalSize, primaryScaling);
        if (savedX is not int x || savedY is not int y || screens.Count == 0)
            return DefaultTopRight(primarySize, primaryWorkingArea);

        var position = new PixelPoint(x, y);

        foreach (var (area, scaling) in screens)
        {
            // Half-open bounds so a point on a shared edge belongs to exactly one screen.
            if (x >= area.X && x < area.Right && y >= area.Y && y < area.Bottom)
                return ClampInto(position, PixelSize.FromSize(logicalSize, scaling), area);
        }

        PixelRect? best = null;
        PixelSize bestSize = primarySize;
        long bestOverlap = 0;
        foreach (var (area, scaling) in screens)
        {
            var size = PixelSize.FromSize(logicalSize, scaling);
            var overlap = new PixelRect(position, size).Intersect(area);
            long overlapArea = (long)overlap.Width * overlap.Height;
            if (overlapArea > bestOverlap)
            {
                bestOverlap = overlapArea;
                best = area;
                bestSize = size;
            }
        }

        return best is PixelRect target
            ? ClampInto(position, bestSize, target)
            : DefaultTopRight(primarySize, primaryWorkingArea);
    }

    /// <summary>Top-right corner of <paramref name="area"/>, inset by <see cref="DefaultMargin"/>.</summary>
    internal static PixelPoint DefaultTopRight(PixelSize windowSize, PixelRect area)
        => ClampInto(
            new PixelPoint(area.Right - windowSize.Width - DefaultMargin, area.Y + DefaultMargin),
            windowSize,
            area);

    /// <summary>Moves <paramref name="position"/> so the window lies inside <paramref name="area"/> (top-left wins if it can't fit).</summary>
    internal static PixelPoint ClampInto(PixelPoint position, PixelSize windowSize, PixelRect area)
    {
        int maxX = Math.Max(area.X, area.Right - windowSize.Width);
        int maxY = Math.Max(area.Y, area.Bottom - windowSize.Height);
        return new PixelPoint(Math.Clamp(position.X, area.X, maxX), Math.Clamp(position.Y, area.Y, maxY));
    }
}
