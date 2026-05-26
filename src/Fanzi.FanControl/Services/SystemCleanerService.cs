using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Windows speed-up utility — cleans caches, temp files, browser caches, recycle bin,
/// trims process working sets (RAM cleaner), flushes DNS cache, and more.
/// Each operation is opt-in via the UI; nothing runs without an explicit click.
/// </summary>
public sealed class SystemCleanerService
{
    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hProcess);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI   = 0x2;
    private const uint SHERB_NOSOUND        = 0x4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>Enumerate all cleanable targets with their current size on disk.</summary>
    public Task<IReadOnlyList<CleanTarget>> ScanAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<CleanTarget>>(() =>
        {
            var list = new List<CleanTarget>();

            void Add(string id, string name, string category, string path, bool isDir = true)
            {
                long size = isDir ? GetFolderSizeSafe(path) : (File.Exists(path) ? new FileInfo(path).Length : 0);
                list.Add(new CleanTarget(id, name, category, path, size, isDir));
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string userTemp = Path.GetTempPath();

            // System temp folders
            Add("user-temp",   "User Temp Files",        "Temp", userTemp);
            Add("win-temp",    "Windows Temp Files",     "Temp", Path.Combine(winDir, "Temp"));
            Add("prefetch",    "Windows Prefetch",       "Temp", Path.Combine(winDir, "Prefetch"));

            // Windows Update cache (often huge)
            Add("wu-cache",    "Windows Update Cache",   "Windows", Path.Combine(winDir, "SoftwareDistribution", "Download"));
            Add("delivery",    "Delivery Optimization Cache", "Windows", Path.Combine(winDir, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"));

            // Thumbnails & icon cache
            Add("thumbs",      "Thumbnail Cache",        "Windows", Path.Combine(local, "Microsoft", "Windows", "Explorer"));

            // Recent files history
            Add("recent",      "Recent Files History",   "Windows", Path.Combine(roaming, "Microsoft", "Windows", "Recent"));

            // Browser caches
            Add("chrome-cache","Chrome Cache",           "Browsers", Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Cache"));
            Add("chrome-code", "Chrome Code Cache",      "Browsers", Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Code Cache"));
            Add("edge-cache",  "Edge Cache",             "Browsers", Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache"));
            Add("edge-code",   "Edge Code Cache",        "Browsers", Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Code Cache"));
            Add("firefox",     "Firefox Cache",          "Browsers", Path.Combine(local, "Mozilla", "Firefox", "Profiles"));
            Add("brave-cache", "Brave Cache",            "Browsers", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Cache"));
            Add("opera-cache", "Opera Cache",            "Browsers", Path.Combine(roaming, "Opera Software", "Opera Stable", "Cache"));

            // App caches
            Add("teams",       "Teams Cache",            "Apps", Path.Combine(roaming, "Microsoft", "Teams", "Cache"));
            Add("discord",     "Discord Cache",          "Apps", Path.Combine(roaming, "discord", "Cache"));
            Add("spotify",     "Spotify Cache",          "Apps", Path.Combine(local, "Spotify", "Storage"));
            Add("nvidia",      "NVIDIA Shader Cache",    "Apps", Path.Combine(local, "NVIDIA", "DXCache"));
            Add("dx-cache",    "DirectX Shader Cache",   "Apps", Path.Combine(local, "D3DSCache"));

            // VS Code workspaces (cache, not settings)
            Add("vscode",      "VS Code Cache",          "Apps", Path.Combine(roaming, "Code", "CachedData"));

            // Crash dumps
            Add("crash-dumps", "Windows Error Reports",  "Misc", Path.Combine(local, "CrashDumps"));
            Add("minidump",    "System Minidumps",       "Misc", Path.Combine(winDir, "Minidump"));

            return (IReadOnlyList<CleanTarget>)list.Where(t => Directory.Exists(t.Path) || File.Exists(t.Path)).ToList();
        }, ct);
    }

    public Task<CleanResult> CleanAsync(IEnumerable<CleanTarget> targets, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            long bytesFreed = 0;
            int filesDeleted = 0;
            int errors = 0;

            foreach (var t in targets)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (t.IsDirectory && Directory.Exists(t.Path))
                    {
                        foreach (var file in Directory.EnumerateFiles(t.Path, "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                long sz = new FileInfo(file).Length;
                                File.SetAttributes(file, FileAttributes.Normal);
                                File.Delete(file);
                                bytesFreed += sz;
                                filesDeleted++;
                            }
                            catch { errors++; }
                        }
                        // Empty subdirs (best-effort)
                        foreach (var d in Directory.EnumerateDirectories(t.Path, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                        {
                            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
                        }
                    }
                    else if (!t.IsDirectory && File.Exists(t.Path))
                    {
                        bytesFreed += new FileInfo(t.Path).Length;
                        File.Delete(t.Path);
                        filesDeleted++;
                    }
                }
                catch { errors++; }
            }

            return new CleanResult(bytesFreed, filesDeleted, errors);
        }, ct);
    }

    /// <summary>
    /// RAM cleaner — calls EmptyWorkingSet on every accessible process. Forces the OS
    /// to trim each process's resident memory back to its minimum, freeing pages back to
    /// the standby/free list. Windows will page back in as needed.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public Task<MemoryTrimResult> TrimAllWorkingSetsAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            long beforeMb = GetUsedMemoryMb();
            int trimmed = 0;
            int failed = 0;

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.HasExited) continue;
                    if (EmptyWorkingSet(p.Handle) != 0) trimmed++;
                    else failed++;
                }
                catch { failed++; }
            }

            Thread.Sleep(800); // let the OS reflect changes
            long afterMb = GetUsedMemoryMb();
            return new MemoryTrimResult(trimmed, failed, beforeMb, afterMb);
        }, ct);
    }

    [SupportedOSPlatform("windows")]
    public Task<int> EmptyRecycleBinAsync()
    {
        return Task.Run(() => SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND));
    }

    public Task<bool> FlushDnsCacheAsync()
    {
        return RunSilent("ipconfig", "/flushdns");
    }

    /// <summary>Triggers Windows built-in disk cleanup wizard for the system drive.</summary>
    public Task<bool> RunWindowsCleanupAsync()
    {
        return RunSilent("cleanmgr", "/sagerun:1");
    }

    /// <summary>Tells Windows to optimise (defrag/TRIM) the system drive.</summary>
    public Task<bool> OptimiseSystemDriveAsync()
    {
        return RunSilent("defrag", "C: /O /U");
    }

    /// <summary>Disable + re-enable Superfetch/SysMain to release prefetch RAM.</summary>
    public Task<bool> ResetSysMainAsync()
    {
        return Task.Run(async () =>
        {
            await RunSilent("sc", "stop SysMain");
            await Task.Delay(500);
            await RunSilent("sc", "start SysMain");
            return true;
        });
    }

    private static async Task<bool> RunSilent(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static long GetFolderSizeSafe(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch { return 0; }
    }

    private static long GetUsedMemoryMb()
    {
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                return (long)((mem.ullTotalPhys - mem.ullAvailPhys) / 1024 / 1024);
            }
        }
        catch { }
        // Fallback
        long sum = 0;
        foreach (var p in Process.GetProcesses())
        {
            try { sum += p.WorkingSet64; } catch { }
        }
        return sum / 1024 / 1024;
    }
}

public sealed record CleanTarget(string Id, string Name, string Category, string Path, long Bytes, bool IsDirectory)
{
    public string SizeDisplay => Bytes switch
    {
        < 1024 => $"{Bytes} B",
        < 1024 * 1024 => $"{Bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{Bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{Bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };
}

public sealed record CleanResult(long BytesFreed, int FilesDeleted, int Errors)
{
    public string Summary => $"Freed {BytesFreed / 1024.0 / 1024.0:F1} MB · {FilesDeleted} files deleted · {Errors} errors";
}

public sealed record MemoryTrimResult(int ProcessesTrimmed, int Failed, long BeforeMb, long AfterMb)
{
    public string Summary => $"Trimmed {ProcessesTrimmed} processes ({Failed} skipped). RAM: {BeforeMb} → {AfterMb} MB ({BeforeMb - AfterMb:+0;-0;0} MB freed)";
}
