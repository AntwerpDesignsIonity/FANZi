using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fanzi.FanControl.Services;

/// <summary>
/// "Start with Windows". FANZi requires Administrator (app.manifest), and Windows silently skips
/// elevated apps listed under HKCU\...\Run — so startup uses a logon scheduled task with
/// "Run with highest privileges" instead (same task the installer creates).
/// </summary>
public static class StartupService
{
    internal const string TaskName = "FANZI";
    private const string LegacyRunValueName = "FANZI";
    private const string RunRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(15);

    public static bool IsRegistered
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;
            return RunSchtasks(new[] { "/Query", "/TN", TaskName }, out _) == 0;
        }
    }

    /// <summary>Creates or deletes the logon task. Returns true when the requested state was reached.</summary>
    public static bool SetStartup(bool enable)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        RemoveLegacyRunValue();

        if (enable)
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath))
            {
                AppLog.Warn("Start with Windows: could not determine the FANZi executable path.");
                return false;
            }

            int code = RunSchtasks(BuildCreateArguments(exePath), out string output);
            if (code == 0)
            {
                AppLog.Info($"Start with Windows: logon task '{TaskName}' registered for {exePath}.");
                return true;
            }

            AppLog.Warn($"Start with Windows: schtasks /Create failed (exit {code}): {output}");
            return false;
        }
        else
        {
            int code = RunSchtasks(new[] { "/Delete", "/F", "/TN", TaskName }, out string output);
            // Deleting a task that doesn't exist is also "disabled".
            if (code == 0 || !IsRegistered)
            {
                AppLog.Info($"Start with Windows: logon task '{TaskName}' removed.");
                return true;
            }

            AppLog.Warn($"Start with Windows: schtasks /Delete failed (exit {code}): {output}");
            return false;
        }
    }

    /// <summary>schtasks arguments for the elevated logon task (each entry is one argv element).</summary>
    internal static IReadOnlyList<string> BuildCreateArguments(string exePath) => new[]
    {
        "/Create", "/F", "/RL", "HIGHEST", "/SC", "ONLOGON", "/TN", TaskName,
        "/TR", $"\"{exePath}\" --minimized",
    };

    private static int RunSchtasks(IReadOnlyList<string> arguments, out string output)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null)
            {
                output = "schtasks.exe could not be started.";
                return -1;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(SchtasksTimeout))
            {
                try { process.Kill(); } catch { }
                output = "schtasks.exe timed out.";
                return -1;
            }

            output = (stderr.Result + " " + stdout.Result).Trim();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            output = $"{ex.GetType().Name}: {ex.Message}";
            return -1;
        }
    }

    /// <summary>Older FANZi versions wrote an HKCU Run value that Windows never honoured for an elevated app.</summary>
    private static void RemoveLegacyRunValue()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RemoveLegacyRunValueWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveLegacyRunValueWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true);
            if (key?.GetValue(LegacyRunValueName) is not null)
            {
                key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
                AppLog.Info("Start with Windows: removed legacy HKCU Run entry.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Start with Windows: could not remove legacy HKCU Run entry", ex);
        }
    }
}
