using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Hands the single-instance lock from an older FANZi build to a newer one (v4).
/// <para>
/// Before v4 a second launch only asked the running instance to show its window — so starting a new
/// portable exe while the old version sat in the tray simply brought the old version back. Now the newer
/// build asks every older FANZi process to exit through <see cref="ExitEventName"/> (v4+ listen and shut down
/// gracefully, restoring automatic fan control), waits, and only terminates builds that predate the event.
/// Same or newer builds are never touched.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class UpgradeHandover
{
    /// <summary>Set by a newer build: running v4+ instances shut down gracefully.</summary>
    public const string ExitEventName = "FANZI_IONITY_Exit";

    internal static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(8);

    /// <summary>True when the version string is lower than <paramref name="current"/> (both "x.y.z", suffixes ignored).</summary>
    internal static bool IsOlder(string? candidate, string current)
    {
        if (!TryParse(candidate, out var c) || !TryParse(current, out var me)) return false;
        return c < me;
    }

    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        string core = text.Trim().TrimStart('v', 'V').Split('+', '-', ' ')[0];
        return Version.TryParse(core, out version!);
    }

    /// <summary>
    /// Closes older FANZi processes. Returns true when at least one was found and is now gone (the caller then
    /// retries the single-instance mutex). Never throws.
    /// </summary>
    public static bool ReplaceOlderInstances(string currentVersion)
    {
        try
        {
            int me = Environment.ProcessId;
            var older = new List<(Process Process, string Version)>();
            foreach (var p in Process.GetProcesses())
            {
                if (p.Id == me || !p.ProcessName.StartsWith("fanzi", StringComparison.OrdinalIgnoreCase)) { p.Dispose(); continue; }
                string? version = null;
                try { version = p.MainModule?.FileVersionInfo.ProductVersion; }
                catch (Exception) { /* access denied (other user) — leave it alone */ }

                if (version is not null && IsOlder(version, currentVersion)) older.Add((p, version));
                else p.Dispose();
            }

            if (older.Count == 0) return false;

            AppLog.Info($"Upgrade: replacing {older.Count} older FANZi instance(s) " +
                        $"({string.Join(", ", older.Select(o => $"pid {o.Process.Id} v{o.Version}"))}) with v{currentVersion}");

            // v4+ instances listen for this and exit cleanly (fans back to automatic first). It is a manual-reset
            // event shared by name, so it is reset below — otherwise this instance's own listener would fire.
            EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent);
            exitEvent?.Set();

            var deadline = DateTime.UtcNow + GracefulWait;
            foreach (var (process, version) in older)
            {
                using (process)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left > TimeSpan.Zero && process.WaitForExit(left)) continue;
                    try
                    {
                        // Builds older than v4 don't listen for the exit event. This instance takes fan control
                        // over immediately after start-up, so nothing is left at a stale duty for long.
                        process.Kill(entireProcessTree: false);
                        process.WaitForExit(3000);
                        AppLog.Info($"Upgrade: closed FANZi v{version} (pid {process.Id})");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"Upgrade: could not close FANZi v{version} (pid {process.Id})", ex);
                    }
                }
            }
            try { exitEvent?.Reset(); } finally { exitEvent?.Dispose(); }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Upgrade handover failed", ex);
            return false;
        }
    }

    /// <summary>Waits on a background thread for <see cref="ExitEventName"/> and runs <paramref name="onExit"/> once.</summary>
    public static void StartExitListener(Action onExit)
    {
        EventWaitHandle handle;
        try { handle = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName); }
        catch (Exception ex)
        {
            AppLog.Warn("Could not create the upgrade exit event", ex);
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                handle.WaitOne();
                AppLog.Info("A newer FANZi build asked this instance to exit — shutting down.");
                onExit();
            }
            catch (Exception ex) { AppLog.Warn("Upgrade exit listener failed", ex); }
        })
        {
            IsBackground = true,
            Name = "FANZI upgrade exit listener",
        };
        thread.Start();
    }
}
