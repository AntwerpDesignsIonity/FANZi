using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public sealed class TrayTooltipFormatterTests
{
    [Fact]
    public void Format_AllReadings()
    {
        Assert.Equal("FANZi — CPU 62°C · GPU 55°C · Health 94", TrayTooltipFormatter.Format(62, 55, 94));
    }

    [Fact]
    public void Format_RoundsHalfAwayFromZero()
    {
        Assert.Equal("FANZi — CPU 63°C · GPU 55°C · Health 95", TrayTooltipFormatter.Format(62.5, 54.6, 94.5));
    }

    [Fact]
    public void Format_MissingReadingsShowDashes()
    {
        Assert.Equal("FANZi — CPU -- · GPU -- · Health --", TrayTooltipFormatter.Format(null, null, null));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Format_InvalidReadingsShowDashes(double invalid)
    {
        Assert.Equal("FANZi — CPU -- · GPU 40°C · Health --", TrayTooltipFormatter.Format(invalid, 40, invalid));
    }

    [Fact]
    public void Format_IsCultureInvariant()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("FANZi — CPU 70°C · GPU 65°C · Health 80", TrayTooltipFormatter.Format(70.2, 65, 80));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void WithAlert_NoAlert_ReturnsTheReadings()
    {
        string tip = TrayTooltipFormatter.Format(62, 55, 94);
        Assert.Equal(tip, TrayTooltipFormatter.WithAlert(tip, null));
        Assert.Equal(tip, TrayTooltipFormatter.WithAlert(tip, "  "));
    }

    [Fact]
    public void WithAlert_AppendsAWarningLine()
    {
        string tip = TrayTooltipFormatter.Format(96, 88, 41);
        Assert.Equal(tip + "\n⚠ CPU temperature high: 96°C", TrayTooltipFormatter.WithAlert(tip, "CPU temperature high: 96°C"));
    }

    [Fact]
    public void WithAlert_LongAlert_IsTrimmedToTheWindowsLimit()
    {
        string tip = TrayTooltipFormatter.Format(96, 88, 41);
        string alert = "OpenRGB problem: " + new string('x', 300) + "\r\nsecond line";

        string combined = TrayTooltipFormatter.WithAlert(tip, alert);

        Assert.Equal(TrayTooltipFormatter.MaxLength, combined.Length);
        Assert.StartsWith(tip + "\n⚠ OpenRGB problem: ", combined);
        Assert.EndsWith("…", combined);
        Assert.DoesNotContain('\r', combined);
    }

    [Fact]
    public void Format_FitsWindowsTooltipLimit()
    {
        // NOTIFYICONDATA.szTip holds 128 chars including the terminator.
        Assert.True(TrayTooltipFormatter.Format(105.4, 99.9, 100).Length < 128);
        Assert.True(TrayTooltipFormatter.Default.Length < 128);
    }
}
