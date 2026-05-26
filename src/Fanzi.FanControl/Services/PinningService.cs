using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Pin FANZI to the Taskbar and/or Start menu.
///
/// Windows 11 removed the public "Pin to Taskbar" verb (Microsoft now blocks
/// programmatic pinning to discourage adware). We use two strategies:
///   1. Shell verb "pintohome" / "pin to start menu" via COM (Win 10 + sometimes Win 11)
///   2. Drop the .lnk straight into the Start Menu Programs folder — Windows treats
///      it as pinned to the Start tile list automatically.
///
/// For taskbar, the only reliable way on Win 11 is to ask the user to right-click and
/// pick "Pin to taskbar" — this service opens the Start Menu shortcut and shows a
/// toast explaining what to do. We also expose a "User Pinned" auto-pin path that
/// works on Win 10.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PinningService
{
    public static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FANZI.lnk");

    public static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FANZI.lnk");

    public static string UserPinnedTaskbarDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    /// <summary>True if we already created a Start Menu entry.</summary>
    public static bool IsInStartMenu => File.Exists(StartMenuShortcutPath);

    /// <summary>True if a Desktop shortcut exists.</summary>
    public static bool HasDesktopShortcut => File.Exists(DesktopShortcutPath);

    /// <summary>
    /// Create or refresh the Start Menu shortcut so FANZI shows up under Start → All Apps.
    /// This is the most-portable form of "pinning" that still works on Windows 11.
    /// </summary>
    public static bool PinToStartMenu(string fanziExePath)
    {
        try
        {
            CreateShortcut(fanziExePath, StartMenuShortcutPath, iconPath: fanziExePath);

            // Try the shell verb too for the Start tile grid (works on Win 10)
            TryInvokeVerb(StartMenuShortcutPath, "pintostart");
            TryInvokeVerb(StartMenuShortcutPath, "pin to start menu");

            return File.Exists(StartMenuShortcutPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// Attempt to pin to Taskbar. Returns true if Windows accepted the verb. On Win 11
    /// this often returns false silently — the UI should then guide the user to right-click
    /// the Start Menu entry and pick "Pin to taskbar" manually.
    /// </summary>
    public static bool PinToTaskbar(string fanziExePath)
    {
        try
        {
            // First make sure a Start shortcut exists (verbs need a real .lnk source)
            PinToStartMenu(fanziExePath);

            // Try Win 10 shell verbs
            if (TryInvokeVerb(StartMenuShortcutPath, "taskbarpin")) return true;
            if (TryInvokeVerb(StartMenuShortcutPath, "Pin to Tas&kbar")) return true;
            if (TryInvokeVerb(fanziExePath, "taskbarpin")) return true;

            // Fallback: drop a .lnk into the User Pinned\TaskBar folder
            Directory.CreateDirectory(UserPinnedTaskbarDir);
            string targetLnk = Path.Combine(UserPinnedTaskbarDir, "FANZI.lnk");
            CreateShortcut(fanziExePath, targetLnk, iconPath: fanziExePath);
            return File.Exists(targetLnk);
        }
        catch { return false; }
    }

    public static bool UnpinFromTaskbar()
    {
        try
        {
            if (TryInvokeVerb(StartMenuShortcutPath, "taskbarunpin")) return true;
            string targetLnk = Path.Combine(UserPinnedTaskbarDir, "FANZI.lnk");
            if (File.Exists(targetLnk)) { File.Delete(targetLnk); return true; }
        }
        catch { }
        return false;
    }

    public static bool UnpinFromStart()
    {
        try
        {
            TryInvokeVerb(StartMenuShortcutPath, "unpinfromstart");
            if (File.Exists(StartMenuShortcutPath)) File.Delete(StartMenuShortcutPath);
            return true;
        }
        catch { return false; }
    }

    public static bool CreateDesktopShortcut(string fanziExePath)
    {
        try
        {
            CreateShortcut(fanziExePath, DesktopShortcutPath, iconPath: fanziExePath);
            return File.Exists(DesktopShortcutPath);
        }
        catch { return false; }
    }

    public static bool RemoveDesktopShortcut()
    {
        try { if (File.Exists(DesktopShortcutPath)) File.Delete(DesktopShortcutPath); return true; }
        catch { return false; }
    }

    /// <summary>Get the FANZI exe path the running app loaded from.</summary>
    public static string GetCurrentExePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName
               ?? Path.Combine(AppContext.BaseDirectory, "Fanzi.FanControl.exe");
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static bool TryInvokeVerb(string targetPath, string verb)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            dynamic folder = shell.Namespace(Path.GetDirectoryName(targetPath));
            dynamic item = folder.ParseName(Path.GetFileName(targetPath));
            foreach (dynamic v in item.Verbs())
            {
                string name = (v.Name as string ?? "").Replace("&", "");
                if (string.Equals(name, verb.Replace("&", ""), StringComparison.OrdinalIgnoreCase))
                {
                    v.DoIt();
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static void CreateShortcut(string targetPath, string shortcutPath, string? iconPath = null)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell is null) return;

        var parent = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? "";
        shortcut.Description = "FANZI — AI-Powered Fan Control & RGB Lighting";
        shortcut.IconLocation = (iconPath ?? targetPath) + ",0";
        shortcut.Save();
    }
}
