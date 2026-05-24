using Fanzi.FanControl.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public static class ProfileExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<string> ExportAsync(FanProfile profile, string filePath)
    {
        try
        {
            string json = JsonSerializer.Serialize(profile, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);
            return $"Exported '{profile.Name}' to {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            return $"Export failed: {ex.Message}";
        }
    }

    public static async Task<(FanProfile? Profile, string Message)> ImportAsync(string filePath)
    {
        try
        {
            string json = await File.ReadAllTextAsync(filePath);
            var profile = JsonSerializer.Deserialize<FanProfile>(json, JsonOptions);
            if (profile is null)
                return (null, "Import failed: invalid profile data.");

            profile.Id = Guid.NewGuid().ToString("N");
            profile.Name = $"{profile.Name} (imported)";
            return (profile, $"Imported '{profile.Name}' successfully.");
        }
        catch (Exception ex)
        {
            return (null, $"Import failed: {ex.Message}");
        }
    }
}
