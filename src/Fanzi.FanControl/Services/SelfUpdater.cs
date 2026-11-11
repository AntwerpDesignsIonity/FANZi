using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// 4.5.1 in-place update (Windows). The running exe cannot be overwritten, but it can be renamed: the current file
/// becomes <c>name.exe.old</c>, the verified download takes its path, and the new build is started from there. Being
/// newer, it asks this instance to shut down through <see cref="UpgradeHandover"/> (fans go back to automatic) and takes
/// over. The <c>.old</c> file is removed on the next start. If the swap fails the original file is put back.
/// </summary>
public static class SelfUpdater
{
    public const string OldSuffix = ".old";

    /// <summary>Folder downloads are staged in (outside the install folder, per user).</summary>
    public static string StagingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FANZI", "updates");

    /// <summary>
    /// Finds the SHA-256 of <paramref name="fileName"/> in a SHA256SUMS file ("HASH  NAME  (n bytes)" or "HASH *NAME").
    /// Null when the file is not listed. Pure.
    /// </summary>
    public static string? FindSha256(string? sumsText, string fileName)
    {
        if (string.IsNullOrWhiteSpace(sumsText)) return null;
        foreach (string raw in sumsText.Split('\n'))
        {
            string[] parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            string hash = parts[0];
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;
            if (string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase))
                return hash.ToUpperInvariant();
        }
        return null;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Replaces <paramref name="currentExe"/> with <paramref name="newExe"/>: current → current.old, new → current.
    /// Throws when it cannot; the original file is back in place in that case.
    /// </summary>
    public static void Swap(string currentExe, string newExe)
    {
        if (!File.Exists(newExe)) throw new FileNotFoundException("The downloaded update is missing.", newExe);
        string old = currentExe + OldSuffix;

        if (File.Exists(old)) File.Delete(old); // left from an earlier update; no longer running
        File.Move(currentExe, old);
        try
        {
            // The staging folder may be on another volume: copy, then drop the download.
            File.Copy(newExe, currentExe, overwrite: false);
        }
        catch
        {
            try { if (File.Exists(currentExe)) File.Delete(currentExe); } catch { /* fall through to restore */ }
            File.Move(old, currentExe);
            throw;
        }
        try { File.Delete(newExe); } catch { /* staging leftovers are cleaned on the next start */ }
    }

    /// <summary>Removes <c>.old</c> left by an update and stale downloads. Called at startup; never throws.</summary>
    public static void CleanupAfterUpdate(string? currentExe)
    {
        try
        {
            if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe + OldSuffix))
            {
                File.Delete(currentExe + OldSuffix);
                AppLog.Info("Update: removed the previous version's file.");
            }
        }
        catch (Exception ex) { AppLog.Info("Update: the previous version's file is still in use — " + ex.Message); }

        try
        {
            if (Directory.Exists(StagingDirectory))
                foreach (string f in Directory.GetFiles(StagingDirectory))
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2)) File.Delete(f);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Keeps "Apps &amp; features" in step after a self-update: when this exe sits in the folder the installer registered,
    /// the registered version is set to the running one. Never throws.
    /// </summary>
    public static void SyncInstalledVersion(string? currentExe, string version)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(currentExe)) return;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FANZI", writable: true);
            if (key?.GetValue("InstallLocation") is not string location) return;
            string here = Path.GetDirectoryName(Path.GetFullPath(currentExe)) ?? "";
            if (!string.Equals(Path.GetFullPath(location).TrimEnd('\\'), here.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            if (key.GetValue("DisplayVersion") as string == version) return;
            key.SetValue("DisplayVersion", version);
            AppLog.Info($"Update: registered version set to {version}.");
        }
        catch (Exception ex) { AppLog.Info("Update: could not refresh the registered version — " + ex.Message); }
    }

    /// <summary>Starts the freshly swapped-in exe; it closes this (older) instance through the upgrade handover.</summary>
    public static void StartNewVersion(string exePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        });
    }
}
