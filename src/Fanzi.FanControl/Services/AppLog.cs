using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Minimal application log. Writes one file per day to
/// %APPDATA%\FANZI\logs\fanzi-yyyyMMdd.log and keeps the last 7 days.
/// Thread-safe and never throws — logging must not be able to crash the app.
/// </summary>
public static class AppLog
{
    internal const string FilePrefix = "fanzi-";
    internal const string FileExtension = ".log";
    internal const int RetentionDays = 7;

    private static readonly object Sync = new();

    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FANZI", "logs");

    private static readonly Func<DateTime> DefaultClock = () => DateTime.Now;

    private static string _directory = DefaultDirectory;

    private static Func<DateTime> _now = DefaultClock;

    // Date of the file currently being written; when it changes we prune old files.
    private static DateTime _currentFileDate = DateTime.MinValue;

    public static string LogDirectory
    {
        get { lock (Sync) return _directory; }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>Redirects logging (tests only). Passing null keeps the current value.</summary>
    internal static void Configure(string? directory = null, Func<DateTime>? clock = null)
    {
        lock (Sync)
        {
            if (directory != null) _directory = directory;
            if (clock != null) _now = clock;
            _currentFileDate = DateTime.MinValue;
        }
    }

    /// <summary>Restores the default directory (%APPDATA%\FANZI\logs) and clock (tests only).</summary>
    internal static void ResetToDefaults()
    {
        lock (Sync)
        {
            _directory = DefaultDirectory;
            _now = DefaultClock;
            _currentFileDate = DateTime.MinValue;
        }
    }

    internal static string GetLogFilePath(DateTime date) =>
        Path.Combine(LogDirectory, FilePrefix + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + FileExtension);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Sync)
            {
                DateTime now = _now();
                Directory.CreateDirectory(_directory);

                if (now.Date != _currentFileDate)
                {
                    _currentFileDate = now.Date;
                    PruneOldFiles(now.Date);
                }

                var sb = new StringBuilder();
                sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                  .Append(" [").Append(level.PadRight(5)).Append("] ")
                  .Append("[T").Append(Environment.CurrentManagedThreadId).Append("] ")
                  .Append(message);
                if (ex != null)
                    sb.AppendLine().Append("    ").Append(ex.ToString().Replace("\n", "\n    "));
                sb.AppendLine();

                string path = Path.Combine(_directory,
                    FilePrefix + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + FileExtension);

                // FileShare.ReadWrite so the log can be tailed / read while the app runs.
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // Never throw from logging.
        }
    }

    /// <summary>Deletes fanzi-yyyyMMdd.log files older than the retention window (by file-name date).</summary>
    private static void PruneOldFiles(DateTime today)
    {
        try
        {
            DateTime cutoff = today.AddDays(-(RetentionDays - 1));
            foreach (string file in Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension))
            {
                string stamp = Path.GetFileNameWithoutExtension(file).Substring(FilePrefix.Length);
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime fileDate)
                    && fileDate < cutoff)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch
        {
        }
    }
}
