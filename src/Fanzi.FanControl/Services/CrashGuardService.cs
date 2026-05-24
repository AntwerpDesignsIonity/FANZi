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

    public static void Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Directory.CreateDirectory(CrashLogDir);
        CleanOldLogs();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        WriteCrashLog(ex, "UnhandledException", e.IsTerminating);

        if (e.IsTerminating && _restartAttempts < MaxRestartAttempts)
        {
            _restartAttempts++;
            AttemptRestart();
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, "UnobservedTaskException", isTerminating: false);
        e.SetObserved();
    }

    public static void WriteCrashLog(Exception? ex, string source, bool isTerminating)
    {
        try
        {
            string filename = $"crash_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{source}.log";
            string path = Path.Combine(CrashLogDir, filename);
            string content = $"""
                FANZI Crash Report
                ==================
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

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--recovered",
                UseShellExecute = true,
            };

            Process.Start(startInfo);
        }
        catch
        {
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
