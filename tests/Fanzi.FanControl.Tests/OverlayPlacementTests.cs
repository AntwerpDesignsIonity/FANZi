using Avalonia;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public sealed class OverlayPlacementTests
{
    private static readonly PixelSize Window = new(296, 236);

    // 1920x1080 primary with a 40 px taskbar at the bottom, and a 2560x1440 monitor to its left.
    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);
    private static readonly PixelRect Left = new(-2560, 0, 2560, 1400);
    private static readonly PixelRect[] Both = { Primary, Left };

    [Fact]
    public void NoSavedPosition_DefaultsToTopRightOfPrimary()
    {
        var p = OverlayPlacement.Resolve(null, null, Window, Both, Primary);

        Assert.Equal(new PixelPoint(1920 - 296 - OverlayPlacement.DefaultMargin, OverlayPlacement.DefaultMargin), p);
    }

    [Fact]
    public void OnlyOneCoordinateSaved_UsesDefault()
    {
        var p = OverlayPlacement.Resolve(100, null, Window, Both, Primary);

        Assert.Equal(OverlayPlacement.DefaultTopRight(Window, Primary), p);
    }

    [Fact]
    public void SavedPositionInsideScreen_IsKept()
    {
        Assert.Equal(new PixelPoint(400, 300), OverlayPlacement.Resolve(400, 300, Window, Both, Primary));
    }

    [Fact]
    public void SavedPositionOnSecondaryScreen_IsKept()
    {
        Assert.Equal(new PixelPoint(-2000, 100), OverlayPlacement.Resolve(-2000, 100, Window, Both, Primary));
    }

    [Fact]
    public void PartlyOffRightEdge_IsClampedInside()
    {
        var p = OverlayPlacement.Resolve(1800, 500, Window, Both, Primary);

        Assert.Equal(new PixelPoint(1920 - 296, 500), p);
    }

    [Fact]
    public void BehindTaskbar_IsClampedAboveIt()
    {
        var p = OverlayPlacement.Resolve(500, 1000, Window, Both, Primary);

        Assert.Equal(new PixelPoint(500, 1040 - 236), p);
    }

    [Fact]
    public void AboveTopEdge_IsClampedDown()
    {
        Assert.Equal(new PixelPoint(500, 0), OverlayPlacement.Resolve(500, -50, Window, Both, Primary));
    }

    [Fact]
    public void StraddlingTwoScreens_ClampsIntoTheOneItOverlapsMost()
    {
        // 246 px on the left monitor, 50 px on the primary → stays on the left monitor.
        var p = OverlayPlacement.Resolve(-246, 200, Window, Both, Primary);

        Assert.Equal(new PixelPoint(-296, 200), p);
    }

    [Fact]
    public void MonitorUnplugged_FallsBackToDefault()
    {
        // Saved on the left monitor, which is gone now.
        var p = OverlayPlacement.Resolve(-2000, 100, Window, new[] { Primary }, Primary);

        Assert.Equal(OverlayPlacement.DefaultTopRight(Window, Primary), p);
    }

    [Fact]
    public void NoScreensKnown_UsesDefault()
    {
        Assert.Equal(OverlayPlacement.DefaultTopRight(Window, Primary),
            OverlayPlacement.Resolve(400, 300, Window, Array.Empty<PixelRect>(), Primary));
    }

    // Mixed DPI: primary 1920x1040 @100 %, a 150 % monitor to the right (3840x2120 working area).
    private static readonly Size Logical = new(296, 236);
    private static readonly PixelRect HiDpiRight = new(1920, 0, 3840, 2120);
    private static readonly (PixelRect, double)[] MixedDpi = { (Primary, 1.0), (HiDpiRight, 1.5) };

    [Fact]
    public void MixedDpi_ClampUsesScalingOfTheScreenHoldingTheSavedPoint()
    {
        // Near the right edge of the 150 % monitor: physical width is 444, not 296.
        var p = OverlayPlacement.Resolve(5600, 1900, Logical, MixedDpi, Primary, 1.0);

        Assert.Equal(new PixelPoint(1920 + 3840 - 444, 2120 - 354), p);
    }

    [Fact]
    public void MixedDpi_PrimaryKeepsItsOwnScaling()
    {
        var p = OverlayPlacement.Resolve(1800, 500, Logical, MixedDpi, Primary, 1.0);

        Assert.Equal(new PixelPoint(1920 - 296, 500), p);
    }

    [Fact]
    public void MixedDpi_DefaultUsesPrimaryScaling()
    {
        var p = OverlayPlacement.Resolve(null, null, Logical, new[] { (HiDpiRight, 1.5), (Primary, 1.25) }, Primary, 1.25);

        Assert.Equal(new PixelPoint(1920 - 370 - OverlayPlacement.DefaultMargin, OverlayPlacement.DefaultMargin), p);
    }

    [Fact]
    public void WindowLargerThanArea_PinsToTopLeft()
    {
        var tiny = new PixelRect(10, 20, 100, 100);

        Assert.Equal(new PixelPoint(10, 20), OverlayPlacement.ClampInto(new PixelPoint(500, 500), Window, tiny));
    }

    [Fact]
    public void Default_OnOffsetPrimary_UsesAreaOrigin()
    {
        var area = new PixelRect(100, 50, 1280, 720);

        Assert.Equal(new PixelPoint(100 + 1280 - 296 - 16, 50 + 16), OverlayPlacement.DefaultTopRight(Window, area));
    }
}
