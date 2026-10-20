using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public sealed class AppVersionTests
{
    [Fact]
    public void Display_ComesFromTheAssembly_AndIs400()
    {
        Assert.Equal("4.3.1", AppVersion.Display);
        Assert.Equal("v4.3", AppVersion.ShortLabel);
        Assert.Equal("FANZi IO-nity v4.3.1", AppVersion.ProductTitle);
        Assert.Equal(new Version(4, 3, 1, 0), typeof(AppVersion).Assembly.GetName().Version);
    }

    [Theory]
    [InlineData("2.2.0", "2.2")]
    [InlineData("2.2.0-beta.1", "2.2")]
    [InlineData("10.4", "10.4")]
    [InlineData("3", "3")]
    public void ToShort_KeepsMajorMinor(string version, string expected)
    {
        Assert.Equal(expected, AppVersion.ToShort(version));
    }
}
