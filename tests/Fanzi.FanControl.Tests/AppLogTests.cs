using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>
/// AppLog is process-global. Every test that configures it — or runs production code that
/// writes to it — joins this collection, which never runs in parallel with any other test.
/// </summary>
[CollectionDefinition("AppLog", DisableParallelization = true)]
public sealed class AppLogCollection
{
}

/// <summary>Points AppLog at a temp folder for one test and restores the defaults afterwards.</summary>
internal sealed class TempAppLog : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "fanzi-applog-scope-" + Guid.NewGuid().ToString("N"));

    public TempAppLog() => AppLog.Configure(Directory);

    public string ReadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return "";
        return string.Concat(System.IO.Directory.GetFiles(Directory, "*.log").Select(File.ReadAllText));
    }

    public void Dispose()
    {
        AppLog.ResetToDefaults();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
    }
}

[Collection("AppLog")]
public sealed class AppLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fanzi-applog-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        AppLog.ResetToDefaults();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ResetToDefaults_RestoresAppDataDirectory()
    {
        AppLog.Configure(_dir);
        Assert.Equal(_dir, AppLog.LogDirectory);

        AppLog.ResetToDefaults();

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FANZI", "logs"),
            AppLog.LogDirectory);
    }

    [Fact]
    public void Write_CreatesDailyFileWithLevelsAndException()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0);
        AppLog.Configure(_dir, () => now);

        AppLog.Info("hello info");
        AppLog.Warn("hello warn");
        AppLog.Error("hello error", new InvalidOperationException("boom"));

        string path = Path.Combine(_dir, "fanzi-20261009.log");
        Assert.True(File.Exists(path));
        string text = File.ReadAllText(path);
        Assert.Contains("[INFO ] ", text);
        Assert.Contains("hello info", text);
        Assert.Contains("[WARN ] ", text);
        Assert.Contains("[ERROR] ", text);
        Assert.Contains("InvalidOperationException: boom", text);
    }

    [Fact]
    public void Write_IsThreadSafe()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0);
        AppLog.Configure(_dir, () => now);

        Parallel.For(0, 200, i => AppLog.Info($"line {i}"));

        var lines = File.ReadAllLines(Path.Combine(_dir, "fanzi-20261009.log"));
        Assert.Equal(200, lines.Length);
    }

    [Fact]
    public void Rotation_NewDayStartsNewFileAndDeletesFilesOlderThanSevenDays()
    {
        Directory.CreateDirectory(_dir);
        var keep = new[] { "fanzi-20261003.log", "fanzi-20261008.log" };           // within 7 days of 2026-10-09
        var delete = new[] { "fanzi-20261002.log", "fanzi-20250101.log" };          // older
        foreach (var f in keep.Concat(delete)) File.WriteAllText(Path.Combine(_dir, f), "old");
        File.WriteAllText(Path.Combine(_dir, "unrelated.log"), "x");

        var now = new DateTime(2026, 10, 8, 23, 59, 0);
        AppLog.Configure(_dir, () => now);
        AppLog.Info("day one");
        now = new DateTime(2026, 10, 9, 0, 1, 0);
        AppLog.Info("day two");

        Assert.Contains("day one", File.ReadAllText(Path.Combine(_dir, "fanzi-20261008.log")));
        Assert.Contains("day two", File.ReadAllText(Path.Combine(_dir, "fanzi-20261009.log")));
        foreach (var f in keep) Assert.True(File.Exists(Path.Combine(_dir, f)), f);
        foreach (var f in delete) Assert.False(File.Exists(Path.Combine(_dir, f)), f);
        Assert.True(File.Exists(Path.Combine(_dir, "unrelated.log")));
    }

    [Fact]
    public void Write_NeverThrows_WhenDirectoryIsInvalid()
    {
        // A path under an existing *file* cannot be created as a directory.
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "blocker");
        File.WriteAllText(file, "x");
        AppLog.Configure(Path.Combine(file, "logs"), () => DateTime.Now);

        var ex = Record.Exception(() => AppLog.Error("should not throw", new Exception("x")));
        Assert.Null(ex);
    }
}
