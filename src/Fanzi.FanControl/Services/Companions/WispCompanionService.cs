using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Fanzi.FanControl.Services.Companions;

/// <summary>
/// v4.5.0: WISP (Aura Wisp) as an additional Ionity application inside FANZi. WISP draws the glowing aura
/// around the active window and mirrors it onto the case through the very OpenRGB server FANZi runs, so
/// the two belong together. This service finds the installed WISP (NSIS install, Microsoft Store, portable
/// build next to a known folder or a path the user pointed at), launches it, and tells whether it is running.
/// </summary>
public static class WispCompanionService
{
    public const string DisplayName = "WISP — Aura Wisp";
    public const string ProcessName = "WISP";
    public const string DownloadUrl = "https://ionity.digital/#wisp";
    public const string ReleasesUrl = "https://github.com/Ionity-Global-Pty-Ltd/WISP/releases/latest";

    private static readonly string[] ExeNames = { "WISP.exe", "WISP-Portable-x64.exe" };

    /// <summary>Settings key for a user-chosen WISP path (portable builds live anywhere).</summary>
    public static string PathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FANZI", "wisp-path.txt");

    /// <summary>True while a WISP.exe (installed or portable) is running for this user.</summary>
    public static bool IsRunning()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using var _ = p;
                try
                {
                    if (p.ProcessName.StartsWith("WISP", StringComparison.OrdinalIgnoreCase) && !p.HasExited) return true;
                }
                catch { /* died or access denied */ }
            }
        }
        catch { }
        return false;
    }

    /// <summary>The first WISP executable that exists, or null when none is installed.</summary>
    public static string? Locate()
    {
        foreach (var c in Candidates())
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    public static IEnumerable<string> Candidates()
    {
        // 1. a path the user pointed FANZi at (portable build on D:\ or a USB stick)
        string? pinned = ReadPinnedPath();
        if (pinned is not null) yield return pinned;

        if (!OperatingSystem.IsWindows()) yield break;

        // 2. NSIS per-user / per-machine installs (electron-builder defaults)
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "WISP", "WISP.exe");
        yield return Path.Combine(local, "Programs", "wisp", "WISP.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WISP", "WISP.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WISP", "WISP.exe");

        // 3. Uninstall registry entries (DisplayName "WISP")
        foreach (var r in RegistryCandidates()) yield return r;

        // 4. a portable build next to FANZi itself
        string? here = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(here))
        {
            foreach (var n in ExeNames) yield return Path.Combine(here, n);
            foreach (var n in ExeNames) yield return Path.Combine(here, "WISP", n);
        }
    }

    private static IEnumerable<string> RegistryCandidates()
    {
        var found = new List<string>();
        if (!OperatingSystem.IsWindows()) return found;
        try
        {
            foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var k = uninstall.OpenSubKey(name);
                    var display = k?.GetValue("DisplayName") as string;
                    if (display is null || !display.StartsWith("WISP", StringComparison.OrdinalIgnoreCase)) continue;
                    var loc = k?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(loc)) found.Add(Path.Combine(loc, "WISP.exe"));
                    var icon = k?.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(icon)) found.Add(icon.Split(',')[0].Trim('"'));
                }
            }
        }
        catch { }
        return found;
    }

    public static string? ReadPinnedPath()
    {
        try
        {
            if (!File.Exists(PathFile)) return null;
            var p = File.ReadAllText(PathFile).Trim();
            return p.Length > 0 ? p : null;
        }
        catch { return null; }
    }

    public static void PinPath(string? exePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            if (string.IsNullOrWhiteSpace(exePath)) File.Delete(PathFile);
            else File.WriteAllText(PathFile, exePath);
        }
        catch (Exception ex) { AppLog.Warn("Could not remember the WISP path", ex); }
    }

    /// <summary>Starts WISP (does nothing when it already runs). Returns false when it is not installed.</summary>
    public static bool Launch(out string message)
    {
        if (IsRunning()) { message = "WISP is already running."; return true; }
        var exe = Locate();
        if (exe is null) { message = "WISP is not installed on this PC."; return false; }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
            AppLog.Info($"Launched WISP: {exe}");
            message = "WISP started.";
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Launching WISP failed", ex);
            message = "Could not start WISP: " + ex.Message;
            return false;
        }
    }

    /// <summary>Asks WISP to quit (its tray process). Returns true when something was closed.</summary>
    public static bool Stop()
    {
        bool any = false;
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                using var _ = p;
                try { if (!p.CloseMainWindow()) p.Kill(entireProcessTree: true); any = true; } catch { }
            }
        }
        catch { }
        return any;
    }

    public static string? Version(string? exe)
    {
        try { return exe is null ? null : FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0]; }
        catch { return null; }
    }
}
