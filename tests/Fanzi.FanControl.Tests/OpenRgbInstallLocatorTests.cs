using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.Tests;

public sealed class OpenRgbInstallLocatorTests
{
    [Fact]
    public void ParseUninstallEntries_ReturnsExePathsForOpenRgbEntriesOnly()
    {
        var entries = new (string?, string?)[]
        {
            ("FANZI — AI-Powered Fan Control", @"C:\Program Files\FANZI"),
            ("OpenRGB", @"C:\Program Files\OpenRGB\"),
            ("Some Other App", @"C:\Other"),
            ("openrgb 1.0 (portable)", @"""D:\Tools\OpenRGB"""),
        };

        var result = OpenRgbInstallLocator.ParseUninstallEntries(entries);

        Assert.Equal(new[]
        {
            @"C:\Program Files\OpenRGB\OpenRGB.exe",
            @"D:\Tools\OpenRGB\OpenRGB.exe",
        }, result);
    }

    [Fact]
    public void ParseUninstallEntries_SkipsMissingNameOrLocation()
    {
        var entries = new (string?, string?)[]
        {
            (null, @"C:\OpenRGB"),
            ("OpenRGB", null),
            ("OpenRGB", "   "),
            ("", @"C:\OpenRGB"),
        };

        Assert.Empty(OpenRgbInstallLocator.ParseUninstallEntries(entries));
    }

    [Fact]
    public void ParseUninstallEntries_DeduplicatesCaseInsensitively()
    {
        var entries = new (string?, string?)[]
        {
            ("OpenRGB", @"C:\Program Files\OpenRGB"),
            ("OpenRGB (x64)", @"c:\program files\openrgb"),
        };

        var result = OpenRgbInstallLocator.ParseUninstallEntries(entries);

        Assert.Single(result);
        Assert.Equal(@"C:\Program Files\OpenRGB\OpenRGB.exe", result[0]);
    }

    [Fact]
    public void ParseUninstallEntries_KeepsExeLocationsAndStripsIconIndex()
    {
        var entries = new (string?, string?)[]
        {
            ("OpenRGB", @"E:\Apps\OpenRGB\OpenRGB.exe"),
            ("OpenRGB SDK", @"F:\RGB\OpenRGB.exe,0"),
        };

        var result = OpenRgbInstallLocator.ParseUninstallEntries(entries);

        Assert.Equal(new[] { @"E:\Apps\OpenRGB\OpenRGB.exe", @"F:\RGB\OpenRGB.exe" }, result);
    }

    [Fact]
    public void FindExisting_PrefersSuppliedCandidateThatExists()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fanzi-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, OpenRgbInstallLocator.ExeName);
            File.WriteAllText(exe, "stub");

            string? found = OpenRgbInstallLocator.FindExisting(new[] { Path.Combine(dir, "missing", "OpenRGB.exe"), exe });

            Assert.Equal(exe, found);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
