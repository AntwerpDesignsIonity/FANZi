using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(60, 60)]
    [InlineData(999, 60)]
    public void Normalize_ClampsPollingInterval(int value, int expected)
    {
        var settings = new AppSettings { PollingIntervalSeconds = value };

        settings.Normalize();

        Assert.Equal(expected, settings.PollingIntervalSeconds);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(10, 10)]
    [InlineData(120, 120)]
    [InlineData(5000, 120)]
    public void Normalize_ClampsReducedPollingInterval(int value, int expected)
    {
        var settings = new AppSettings { ReducedPollingIntervalSeconds = value };

        settings.Normalize();

        Assert.Equal(expected, settings.ReducedPollingIntervalSeconds);
    }

    [Fact]
    public void Normalize_FiltersNullProfiles()
    {
        var keep = new FanProfile { Name = "Keep" };
        var settings = new AppSettings { Profiles = new List<FanProfile> { null!, keep, null! } };

        settings.Normalize();

        Assert.Equal(new[] { keep }, settings.Profiles);
    }

    [Fact]
    public void Normalize_NullProfileList_BecomesEmpty()
    {
        var settings = new AppSettings { Profiles = null! };

        settings.Normalize();

        Assert.NotNull(settings.Profiles);
        Assert.Empty(settings.Profiles);
    }

    [Fact]
    public void MigrateThenNormalize_FromJsonWithBadValues()
    {
        const string json = """{ "SettingsVersion": 0, "OverlayTransparent": true, "PollingIntervalSeconds": 0, "ReducedPollingIntervalSeconds": -3, "Profiles": [ null, { "Name": "Gaming" } ] }""";
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        settings.Migrate();
        settings.Normalize();

        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.False(settings.OverlayTransparent);
        Assert.Equal(1, settings.PollingIntervalSeconds);
        Assert.Equal(2, settings.ReducedPollingIntervalSeconds);
        Assert.Equal("Gaming", Assert.Single(settings.Profiles).Name);
    }

    [Fact]
    public void Migrate_V2ToV3_KeepsUserValuesAndAddsOverlayDefaults()
    {
        const string json = """
            { "SettingsVersion": 2, "OverlayTransparent": true, "OverlayOpacity": 0.55, "CloseToTray": false,
              "PollingIntervalSeconds": 7, "OpenRgbPath": "C:\\Tools\\OpenRGB\\OpenRGB.exe",
              "Profiles": [ { "Name": "Gaming" } ], "ActiveProfileId": "abc" }
            """;
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        settings.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.OverlayTransparent);
        Assert.Equal(0.55, settings.OverlayOpacity);
        Assert.False(settings.CloseToTray);
        Assert.Equal(7, settings.PollingIntervalSeconds);
        Assert.Equal(@"C:\Tools\OpenRGB\OpenRGB.exe", settings.OpenRgbPath);
        Assert.Equal("Gaming", Assert.Single(settings.Profiles).Name);
        Assert.Equal("abc", settings.ActiveProfileId);
        Assert.False(settings.OverlayTopmost);
        Assert.Null(settings.OverlayX);
        Assert.Null(settings.OverlayY);
        Assert.True(settings.OverlayShowCpuHistory); // absent in older files → sparkline on
    }

    [Fact]
    public void Migrate_V3_IsIdempotentAndKeepsOverlayValues()
    {
        var settings = new AppSettings
        {
            SettingsVersion = 3, OverlayTopmost = true, OverlayX = -1200, OverlayY = 40,
            OverlayTransparent = true, OverlayOpacity = 0.4,
        };

        settings.Migrate();
        settings.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.OverlayTopmost);
        Assert.Equal(-1200, settings.OverlayX);
        Assert.Equal(40, settings.OverlayY);
        Assert.True(settings.OverlayTransparent);
        Assert.Equal(0.4, settings.OverlayOpacity);
    }

    [Fact]
    public void Migrate_V3ToV4_EnablesToastNotificationsAndKeepsUserValues()
    {
        // A real v3 file: no ToastNotificationsEnabled key at all.
        const string json = """
            { "SettingsVersion": 3, "OverlayTopmost": true, "OverlayX": 1500, "OverlayY": 20, "OverlayShowCpuHistory": false,
              "AiAnomalyDetection": false, "CloseToTray": false, "PollingIntervalSeconds": 5,
              "Profiles": [ { "Name": "Gaming" } ], "ActiveProfileId": "abc" }
            """;
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        settings.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.ToastNotificationsEnabled);
        Assert.True(settings.OverlayTopmost);
        Assert.Equal(1500, settings.OverlayX);
        Assert.Equal(20, settings.OverlayY);
        Assert.False(settings.OverlayShowCpuHistory);
        Assert.False(settings.AiAnomalyDetection);
        Assert.False(settings.CloseToTray);
        Assert.Equal(5, settings.PollingIntervalSeconds);
        Assert.Equal("Gaming", Assert.Single(settings.Profiles).Name);
        Assert.Equal("abc", settings.ActiveProfileId);
    }

    [Fact]
    public void Migrate_V4_KeepsToastsSwitchedOff()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{ "SettingsVersion": 4, "ToastNotificationsEnabled": false }""")!;

        settings.Migrate();
        settings.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.False(settings.ToastNotificationsEnabled); // the user's choice survives every later start
    }

    [Fact]
    public void ToastSetting_RoundTripsThroughJson_AndDefaultsOn()
    {
        Assert.True(new AppSettings().ToastNotificationsEnabled);

        var copy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(new AppSettings { ToastNotificationsEnabled = false }))!;

        Assert.False(copy.ToastNotificationsEnabled);
    }

    [Fact]
    public void Migrate_V4ToV5_AddsReduceMotionAndOverlayModeOff_AndKeepsUserValues()
    {
        // A real v4 file: no ReduceMotion / OverlayCompact keys.
        const string json = """
            { "SettingsVersion": 4, "ToastNotificationsEnabled": false, "OverlayTopmost": true, "OverlayShowCpuHistory": false,
              "OverlayX": 1500, "OverlayY": 20, "OverlayTransparent": true, "OverlayOpacity": 0.6,
              "PollingIntervalSeconds": 5, "Profiles": [ { "Name": "Gaming" } ], "ActiveProfileId": "abc" }
            """;
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        settings.Migrate();

        Assert.Equal(6, AppSettings.CurrentVersion);
        Assert.Equal(6, settings.SettingsVersion);
        Assert.False(settings.ReduceMotion);    // upgraded installs keep their animations
        Assert.False(settings.OverlayCompact);  // ...and the expanded overlay, the only layout before v5
        Assert.False(settings.ToastNotificationsEnabled);
        Assert.True(settings.OverlayTopmost);
        Assert.False(settings.OverlayShowCpuHistory);
        Assert.Equal(1500, settings.OverlayX);
        Assert.Equal(20, settings.OverlayY);
        Assert.True(settings.OverlayTransparent);
        Assert.Equal(0.6, settings.OverlayOpacity);
        Assert.Equal(5, settings.PollingIntervalSeconds);
        Assert.Equal("Gaming", Assert.Single(settings.Profiles).Name);
        Assert.Equal("abc", settings.ActiveProfileId);
    }

    [Fact]
    public void Migrate_V5_KeepsReduceMotionAndCompactOverlay()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{ "SettingsVersion": 5, "ReduceMotion": true, "OverlayCompact": true }""")!;

        settings.Migrate();
        settings.Migrate();

        Assert.Equal(6, settings.SettingsVersion);
        Assert.True(settings.ReduceMotion);
        Assert.True(settings.OverlayCompact);
    }

    [Theory]
    [InlineData(3, 1)]  // the old default moves to the 1 s real-time dashboard
    [InlineData(5, 5)]  // a value the user picked is kept
    [InlineData(1, 1)]
    public void Migrate_V5ToV6_MovesOldDefaultPollingToRealtime(int before, int after)
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            $$"""{ "SettingsVersion": 5, "PollingIntervalSeconds": {{before}} }""")!;

        settings.Migrate();
        settings.Migrate();

        Assert.Equal(after, settings.PollingIntervalSeconds);
        Assert.Equal(6, settings.SettingsVersion);
    }

    [Fact]
    public void Migrate_V6_KeepsAChosen3SecondInterval()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{ "SettingsVersion": 6, "PollingIntervalSeconds": 3 }""")!;
        settings.Migrate();
        Assert.Equal(3, settings.PollingIntervalSeconds);
    }

    [Fact]
    public void Migrate_V1File_GetsMotionAndOverlayModeDefaults()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{ "SettingsVersion": 1, "OverlayTransparent": true }""")!;

        settings.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.False(settings.ReduceMotion);
        Assert.False(settings.OverlayCompact);
        Assert.True(settings.ToastNotificationsEnabled);
    }

    [Fact]
    public void MotionAndOverlayModeSettings_RoundTripThroughJson_AndDefaultOff()
    {
        Assert.False(new AppSettings().ReduceMotion);
        Assert.False(new AppSettings().OverlayCompact);

        var copy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(new AppSettings { ReduceMotion = true, OverlayCompact = true }))!;

        Assert.True(copy.ReduceMotion);
        Assert.True(copy.OverlayCompact);
    }

    [Fact]
    public void OverlaySettings_RoundTripThroughJson()
    {
        var original = new AppSettings { OverlayTopmost = true, OverlayX = 1500, OverlayY = 20, OverlayShowCpuHistory = false };

        var copy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(original))!;

        Assert.False(copy.OverlayShowCpuHistory);
        Assert.True(copy.OverlayTopmost);
        Assert.Equal(1500, copy.OverlayX);
        Assert.Equal(20, copy.OverlayY);
    }

    [Fact]
    public void Normalize_KeepsValidUserValues()
    {
        var settings = new AppSettings { PollingIntervalSeconds = 5, ReducedPollingIntervalSeconds = 30 };

        settings.Normalize();

        Assert.Equal(5, settings.PollingIntervalSeconds);
        Assert.Equal(30, settings.ReducedPollingIntervalSeconds);
    }
}
