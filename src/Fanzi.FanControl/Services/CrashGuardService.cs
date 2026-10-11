using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public static class CrashGuardService
{
    private static readonly string CrashLogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FANZI", "crash-logs");

    private static int _restartAttempts;
    private const int MaxRestartAttempts = 3;

    // Rate-limit + dedup the crash log so an exception that fires every second
    // doesn't spam the disk with thousands of identical files.
    private static DateTime _lastLogWriteUtc = DateTime.MinValue;
    private static string _lastLoggedSignature = "";
    private static readonly TimeSpan LogCooldown = TimeSpan.FromMinutes(1);

    internal const string RecoveredArgument = "--recovered";
    internal const string RestartCountArgumentPrefix = "--restart-count=";
    private static bool _startMinimized;

    /// <param name="restartCount">
    /// How many automatic restarts led to this process (from <c>--restart-count=N</c>). The counter is
    /// carried across processes so a crash loop stops after <see cref="MaxRestartAttempts"/> restarts.
    /// </param>
    public static void Initialize(int restartCount = 0, bool startMinimized = false)
    {
        _restartAttempts = Math.Max(0, restartCount);
        _startMinimized = startMinimized;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Directory.CreateDirectory(CrashLogDir);
        }
        catch
        {
        }
        CleanOldLogs();
        AppLog.Info($"CrashGuard initialised (crash logs: {CrashLogDir}, restart count {_restartAttempts}/{MaxRestartAttempts})");
    }

    /// <summary>True when the process was started by CrashGuard after a crash.</summary>
    internal static bool IsRecoveredStart(string[] args) =>
        args.Any(a => string.Equals(a, RecoveredArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses <c>--restart-count=N</c>; missing, malformed or negative values give 0.</summary>
    internal static int ParseRestartCount(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg is null || !arg.StartsWith(RestartCountArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string value = arg.Substring(RestartCountArgumentPrefix.Length);
            if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int count) && count > 0)
            {
                return count;
            }

            return 0;
        }

        return 0;
    }

    /// <summary>Whether a process that has already been restarted <paramref name="restartCount"/> times may restart again.</summary>
    internal static bool ShouldRestart(int restartCount) => restartCount < MaxRestartAttempts;

    /// <summary>Command-line arguments for the replacement process.</summary>
    internal static string BuildRestartArguments(int nextRestartCount, bool startMinimized) =>
        $"{RecoveredArgument} {RestartCountArgumentPrefix}{nextRestartCount}" + (startMinimized ? " --minimized" : "");

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        AppLog.Error($"CrashGuard: unhandled exception (terminating: {e.IsTerminating})", ex);
        WriteCrashLog(ex, "UnhandledException", e.IsTerminating);

        if (!e.IsTerminating)
            return;

        if (ShouldRestart(_restartAttempts))
        {
            _restartAttempts++;
            AttemptRestart();
        }
        else
        {
            AppLog.Warn($"CrashGuard: not restarting — already restarted {_restartAttempts} times.");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Warn("CrashGuard: unobserved task exception", e.Exception);
        WriteCrashLog(e.Exception, "UnobservedTaskException", isTerminating: false);
        e.SetObserved();
    }

    public static void WriteCrashLog(Exception? ex, string source, bool isTerminating)
    {
        try
        {
            // De-spam: same exception within cooldown → skip writing another log.
            // Always write terminating exceptions though — those matter.
            string sig = $"{source}|{ex?.GetType().Name}|{ex?.Message}";
            if (!isTerminating
                && sig == _lastLoggedSignature
                && DateTime.UtcNow - _lastLogWriteUtc < LogCooldown)
            {
                return;
            }
            _lastLoggedSignature = sig;
            _lastLogWriteUtc = DateTime.UtcNow;

            string filename = $"crash_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{source}.log";
            string path = Path.Combine(CrashLogDir, filename);
            string content = $"""
                FANZI Crash Report
                ==================
                Version: {AppVersion.Display}
                Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}
                Source: {source}
                Terminating: {isTerminating}
                OS: {RuntimeInformation.OSDescription}
                Architecture: {RuntimeInformation.OSArchitecture}
                .NET: {RuntimeInformation.FrameworkDescription}
                Process: {Environment.ProcessPath}
                WorkingSet: {Environment.WorkingSet / 1024 / 1024} MB
                Restart Attempts: {_restartAttempts}/{MaxRestartAttempts}

                Exception:
                {ex?.ToString() ?? "No exception details available"}

                Stack Trace:
                {ex?.StackTrace ?? "N/A"}

                Inner Exception:
                {ex?.InnerException?.ToString() ?? "N/A"}
                """;

            File.WriteAllText(path, content);
            AppLog.Info($"CrashGuard: crash log written to {path}");
        }
        catch
        {
        }
    }

    private static void AttemptRestart()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            // The new process waits (up to 10 s) for this one's single-instance mutex to be released.
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = BuildRestartArguments(_restartAttempts, _startMinimized),
                UseShellExecute = true,
            };

            AppLog.Warn($"CrashGuard: restarting after crash (attempt {_restartAttempts}/{MaxRestartAttempts})");
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            AppLog.Error("CrashGuard: restart failed", ex);
        }
    }

    private static void CleanOldLogs()
    {
        try
        {
            var dir = new DirectoryInfo(CrashLogDir);
            if (!dir.Exists) return;

            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in dir.GetFiles("crash_*.log"))
            {
                if (file.LastWriteTimeUtc < cutoff)
                    file.Delete();
            }
        }
        catch
        {
        }
    }

    public static string GetCrashLogDirectory() => CrashLogDir;

    public static int GetRecentCrashCount()
    {
        try
        {
            var dir = new DirectoryInfo(CrashLogDir);
            if (!dir.Exists) return 0;

            var cutoff = DateTime.UtcNow.AddHours(-1);
            return dir.GetFiles("crash_*.log")
                .Count(f => f.LastWriteTimeUtc > cutoff);
        }
        catch
        {
            return 0;
        }
    }
}
