using System.Runtime.CompilerServices;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>
/// 4.6: runs once when the test assembly loads. Production code under test logs through <see cref="AppLog"/>; before
/// this, every test run appended its deliberate failures ("test storm", "boom", missing tools) to the developer's real
/// %APPDATA%\FANZI\logs file — and the Self-Doctor of an installed FANZi could read them as an error storm.
/// </summary>
internal static class TestLogIsolation
{
    internal static readonly string Directory =
        Path.Combine(Path.GetTempPath(), "fanzi-test-logs-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Initialize()
    {
        AppLog.SetBaselineDirectory(Directory);
        // Automatic RGB take-over is on by default: a test run must never close a lighting app on the machine it runs on.
        Fanzi.FanControl.Services.Rgb.RgbConflictDetector.AllowStop = false;
    }
}
