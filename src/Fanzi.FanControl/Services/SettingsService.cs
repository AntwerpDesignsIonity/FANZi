using Fanzi.FanControl.Models;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    internal static readonly string DefaultSettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FANZI",
        "settings.json");

    private readonly string _settingsFilePath;

    // Saves are serialized so two writers never race on the same file; each save gets a
    // sequence number and a save that was overtaken by a newer one is skipped (last call wins).
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private long _saveSequence;
    private long _lastWrittenSequence;

    public SettingsService()
        : this(DefaultSettingsFilePath)
    {
    }

    public SettingsService(string settingsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);
        _settingsFilePath = settingsFilePath;
    }

    public string SettingsFilePath => _settingsFilePath;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(_settingsFilePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct)
                   ?? new AppSettings();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep the unreadable file so the next save (with defaults) doesn't destroy the user's settings.
            string? backup = BackupCorruptFile();
            AppLog.Error(
                $"Settings file {_settingsFilePath} could not be read — using defaults" +
                (backup is null ? " (backup copy failed)." : $"; original kept as {backup}."),
                ex);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Snapshot now (on the caller's thread) so later mutations of the shared settings
        // object can't race with serialization.
        byte[] json;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings could not be serialized — not saved.", ex);
            return;
        }

        long sequence = Interlocked.Increment(ref _saveSequence);

        await _saveLock.WaitAsync(ct);
        string tempPath = $"{_settingsFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (sequence < _lastWrittenSequence)
            {
                return; // A newer save already reached the disk.
            }

            string? dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(tempPath, json, ct);
            File.Move(tempPath, _settingsFilePath, overwrite: true);
            _lastWrittenSequence = sequence;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
        }
        catch (Exception ex)
        {
            // Settings persistence is best-effort, but never silently.
            AppLog.Error($"Settings could not be saved to {_settingsFilePath}", ex);
            TryDelete(tempPath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private string? BackupCorruptFile()
    {
        try
        {
            string dir = Path.GetDirectoryName(_settingsFilePath) ?? ".";
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string backup = Path.Combine(dir, $"settings.corrupt-{stamp}.json");
            File.Copy(_settingsFilePath, backup, overwrite: true);
            return backup;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
