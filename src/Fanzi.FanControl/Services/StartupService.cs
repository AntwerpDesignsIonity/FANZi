using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fanzi.FanControl.Services;

public static class StartupService
{
    private const string AppName = "FANZI";
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsRegistered
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;
            return IsRegisteredWindows();
        }
    }

    public static void SetStartup(bool enable)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        SetStartupWindows(enable);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRegisteredWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetStartupWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
            if (key is null) return;

            if (enable)
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(AppName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch
        {
        }
    }
}
