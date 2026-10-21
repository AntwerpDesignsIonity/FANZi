using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32;

namespace Fanzi.Installer;

/// <summary>An installed FANZi found on this PC (from the Uninstall registry entry).</summary>
public sealed record ExistingInstall(string Path, string Version, bool StartsWithWindows)
{
    /// <summary>Reads HKLM\…\Uninstall\FANZI (64- and 32-bit views). Null when FANZi is not installed or the folder is gone.</summary>
    [SupportedOSPlatform("windows")]
    public static ExistingInstall? Find()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FANZI");
                if (key?.GetValue("InstallLocation") is not string path || string.IsNullOrWhiteSpace(path)) continue;
                if (!Directory.Exists(path)) continue;
                string version = key.GetValue("DisplayVersion") as string ?? "0.0.0";
                return new ExistingInstall(path, version, HasRunEntry());
            }
            catch
            {
                // A locked-down registry is not a reason to fail the install.
            }
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool HasRunEntry()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            return run?.GetValue("FANZI") is not null;
        }
        catch { return false; }
    }

    /// <summary>-1 / 0 / 1 like CompareTo; "4.3.3" vs "4.4.0". Suffixes ("-beta") and missing parts are ignored.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parse(string v) => (v ?? "").Split('-', '+')[0].Split('.')
            .Select(p => int.TryParse(p, out int n) ? n : 0).Concat(Enumerable.Repeat(0, 4)).Take(4).ToArray();
        var x = Parse(a); var y = Parse(b);
        for (int i = 0; i < 4; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    /// <summary>
    /// Closes every running FANZi (tray included) so its files can be replaced. Returns true when one was running.
    /// FANZi 4.x listens for the named exit event and shuts down gracefully (fans handed back to the BIOS);
    /// anything still alive after 6 s is terminated.
    /// </summary>
    public static bool StopRunningFanzi(Action<string>? log = null)
    {
        var names = new[] { "Fanzi.FanControl", "Run_Ionity", "FANZI" };
        bool any = names.SelectMany(SafeProcesses).Any();
        if (!any) return false;

        EventWaitHandle? exit = null;
        try
        {
            if (OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting("FANZI_IONITY_Exit", out exit))
            {
                exit.Set();
                log?.Invoke("Asked the running FANZi to close (graceful exit event).");
            }
        }
        catch { }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && names.SelectMany(SafeProcesses).Any()) Thread.Sleep(250);

        // Manual-reset event: reset it, or the freshly installed FANZi would read it and close at once.
        try { exit?.Reset(); } catch { }
        exit?.Dispose();

        foreach (var p in names.SelectMany(SafeProcesses))
        {
            try
            {
                log?.Invoke($"Stopping {p.ProcessName} (PID {p.Id}).");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception ex) { log?.Invoke($"Could not stop {p.ProcessName}: {ex.Message}"); }
            finally { p.Dispose(); }
        }
        return true;
    }

    private static Process[] SafeProcesses(string name)
    {
        try
        {
            int self = Environment.ProcessId;
            return Process.GetProcessesByName(name).Where(p => p.Id != self).ToArray();
        }
        catch { return Array.Empty<Process>(); }
    }

    /// <summary>
    /// Replaces a file that may still be locked: the old file is renamed to *.old (Windows allows renaming a running
    /// exe), the new one is written, and *.old is deleted when possible. Retries for ~5 s on sharing violations.
    /// </summary>
    public static void ReplaceFile(string target, Action<string> writeNew)
    {
        string old = target + ".old";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(old)) { try { File.Delete(old); } catch { } }
                if (File.Exists(target)) File.Move(target, File.Exists(old) ? target + $".{DateTime.Now:HHmmss}.old" : old);
                writeNew(target);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }

        foreach (var leftover in Directory.GetFiles(System.IO.Path.GetDirectoryName(target)!, System.IO.Path.GetFileName(target) + "*.old"))
        {
            try { File.Delete(leftover); } catch { /* still mapped by a dying process — next install removes it */ }
        }
    }
}
