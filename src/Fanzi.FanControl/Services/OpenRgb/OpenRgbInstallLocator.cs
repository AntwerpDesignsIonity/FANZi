using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace Fanzi.FanControl.Services.OpenRgb;

/// <summary>
/// Finds an existing OpenRGB installation: well-known folders, the Windows
/// Uninstall / App Paths registry keys and PATH.
/// Shared with Fanzi.Installer (linked source) — BCL only, no app dependencies.
/// </summary>
public static class OpenRgbInstallLocator
{
    public const string ExeName = "OpenRGB.exe";

    internal const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string AppPathsKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OpenRGB.exe";

    /// <summary>Where FANZI downloads its own copy of OpenRGB: %LOCALAPPDATA%\FANZI\OpenRGB.</summary>
    public static string ManagedInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FANZI", "OpenRGB");

    /// <summary>Standard Windows install locations of OpenRGB.exe (FANZI-managed copy first).</summary>
    public static IEnumerable<string> WellKnownExePaths()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(ManagedInstallDirectory, ExeName);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", ExeName);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenRGB", ExeName);
        yield return Path.Combine(localAppData, "Programs", "OpenRGB", ExeName);
        yield return Path.Combine(localAppData, "OpenRGB", ExeName);
    }

    /// <summary>
    /// Pure helper over Uninstall-key values: every entry whose DisplayName contains
    /// "OpenRGB" and has a non-empty InstallLocation yields a candidate OpenRGB.exe path
    /// (order preserved, duplicates removed). Existence is not checked here.
    /// </summary>
    public static IReadOnlyList<string> ParseUninstallEntries(
        IEnumerable<(string? DisplayName, string? InstallLocation)> entries)
    {
        var result = new List<string>();
        foreach (var (displayName, installLocation) in entries)
        {
            if (string.IsNullOrWhiteSpace(displayName) ||
                !displayName.Contains("OpenRGB", StringComparison.OrdinalIgnoreCase))
                continue;

            string? candidate = ToExePath(installLocation);
            if (candidate != null && !result.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                result.Add(candidate);
        }
        return result;
    }

    /// <summary>
    /// Candidate OpenRGB.exe paths from HKLM (64- and 32-bit views) and HKCU Uninstall
    /// entries plus App Paths\OpenRGB.exe. Empty on non-Windows or when the registry
    /// cannot be read.
    /// </summary>
    public static IReadOnlyList<string> GetRegistryCandidates()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();

        var candidates = new List<string>(ParseUninstallEntries(ReadUninstallEntries()));
        foreach (string path in ReadAppPaths())
            if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                candidates.Add(path);
        return candidates;
    }

    /// <summary>First registry candidate that exists on disk, or null.</summary>
    public static string? FindInRegistry() => GetRegistryCandidates().FirstOrDefault(File.Exists);

    /// <summary>First OpenRGB.exe found in a PATH directory, or null.</summary>
    public static string? FindOnPath(string exeName = ExeName)
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), exeName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry — ignore.
                }
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// First existing OpenRGB.exe from <paramref name="preferredCandidates"/>, then the
    /// well-known folders, the registry and PATH. Null when OpenRGB is not installed.
    /// </summary>
    public static string? FindExisting(IEnumerable<string>? preferredCandidates = null)
    {
        foreach (string candidate in preferredCandidates ?? Array.Empty<string>())
            if (SafeExists(candidate)) return candidate;
        foreach (string candidate in WellKnownExePaths())
            if (SafeExists(candidate)) return candidate;
        return FindInRegistry() ?? FindOnPath();
    }

    /// <summary>Strips whitespace/quotes, expands %VARS% and appends OpenRGB.exe to folders.</summary>
    internal static string? ToExePath(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        string trimmed = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"').Trim());
        if (trimmed.Length == 0) return null;

        // Some entries store "C:\...\OpenRGB.exe,0" (icon index) — drop the suffix.
        int comma = trimmed.LastIndexOf(".exe,", StringComparison.OrdinalIgnoreCase);
        if (comma >= 0) trimmed = trimmed.Substring(0, comma + 4);

        try
        {
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : Path.Combine(trimmed, ExeName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool SafeExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(RegistryHive Hive, RegistryView View)> RegistryRoots() => new[]
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    };

    [SupportedOSPlatform("windows")]
    private static List<(string? DisplayName, string? InstallLocation)> ReadUninstallEntries()
    {
        var entries = new List<(string?, string?)>();
        foreach (var (hive, view) in RegistryRoots())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallKeyPath);
                if (uninstall == null) continue;

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = uninstall.OpenSubKey(name);
                        if (sub == null) continue;
                        entries.Add((sub.GetValue("DisplayName") as string, sub.GetValue("InstallLocation") as string));
                    }
                    catch
                    {
                        // Unreadable subkey — skip.
                    }
                }
            }
            catch
            {
                // Hive/view not available — skip.
            }
        }
        return entries;
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ReadAppPaths()
    {
        var paths = new List<string>();
        foreach (var (hive, view) in RegistryRoots())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(AppPathsKeyPath);
                if (key == null) continue;

                // (Default) = full exe path; "Path" = containing folder.
                foreach (string? raw in new[] { key.GetValue(null) as string, key.GetValue("Path") as string })
                {
                    string? candidate = ToExePath(raw);
                    if (candidate != null && !paths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                        paths.Add(candidate);
                }
            }
            catch
            {
            }
        }
        return paths;
    }
}
