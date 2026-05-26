using Fanzi.FanControl.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Disk-backed profile manager. Profiles live under
///   %USERPROFILE%\Ionity Global\Antwerp Designs\Fanzy\Profile\{name}.fanzi.json
/// per the user's organisational hierarchy. Supports save, load, delete, list,
/// and arbitrary-path export/import for sharing profiles between machines.
/// </summary>
public sealed class ProfileManagerService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The branded default location every save lands in.</summary>
    public static string DefaultProfileDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Ionity Global", "Antwerp Designs", "Fanzy", "Profile");

    public const string ProfileExtension = ".fanzi.json";

    public ProfileManagerService()
    {
        try { Directory.CreateDirectory(DefaultProfileDir); } catch { }
    }

    /// <summary>Save a profile to the default directory using its Name as the file.</summary>
    public Task<string> SaveAsync(FanProfile profile, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(DefaultProfileDir);
            string safe = SanitiseFileName(profile.Name);
            string path = Path.Combine(DefaultProfileDir, safe + ProfileExtension);
            File.WriteAllText(path, JsonSerializer.Serialize(profile, Json));
            return path;
        }, ct);
    }

    /// <summary>Save to an arbitrary path (used by Export to File picker).</summary>
    public Task SaveToPathAsync(FanProfile profile, string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(profile, Json));
        }, ct);
    }

    /// <summary>Load a profile by file path (default dir or wherever the user picks).</summary>
    public Task<FanProfile?> LoadFromPathAsync(string path, CancellationToken ct = default)
    {
        return Task.Run<FanProfile?>(() =>
        {
            try
            {
                string content = File.ReadAllText(path);
                return JsonSerializer.Deserialize<FanProfile>(content, Json);
            }
            catch { return null; }
        }, ct);
    }

    /// <summary>List every saved profile in the default directory.</summary>
    public Task<IReadOnlyList<ProfileFileInfo>> ListAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<ProfileFileInfo>>(() =>
        {
            try
            {
                Directory.CreateDirectory(DefaultProfileDir);
                return Directory.EnumerateFiles(DefaultProfileDir, "*" + ProfileExtension)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .Select(fi => new ProfileFileInfo(
                        Name: Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fi.Name)),
                        FullPath: fi.FullName,
                        LastModified: fi.LastWriteTime,
                        SizeBytes: fi.Length))
                    .ToList();
            }
            catch { return new List<ProfileFileInfo>(); }
        }, ct);
    }

    public bool Delete(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath) && fullPath.StartsWith(DefaultProfileDir, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(fullPath);
                return true;
            }
        }
        catch { }
        return false;
    }

    public void OpenProfilesFolder()
    {
        try
        {
            Directory.CreateDirectory(DefaultProfileDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DefaultProfileDir,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private static string SanitiseFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim();
    }
}

public sealed record ProfileFileInfo(string Name, string FullPath, DateTime LastModified, long SizeBytes)
{
    public string SizeDisplay => SizeBytes < 1024 ? $"{SizeBytes} B" : $"{SizeBytes / 1024.0:F1} KB";
    public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");
}
