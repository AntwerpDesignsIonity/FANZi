using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>
/// Keeps OpenRGB's SDK server compatible with FANZi.
/// <para>
/// OpenRGB 1.0 validates the size field of mode packets and rejects those from protocol-4 clients
/// (FANZi talks protocol 4 through OpenRGB.NET) unless <c>Server → legacy_workaround</c> is on.
/// With it off, colours only reached per-LED devices and every mode change — including the
/// "Static" colour that GPUs like the ZOTAC RTX 30-series need — was silently dropped.
/// </para>
/// FANZi launches its server with a private <c>--config</c> folder where the flag is on, and also
/// flips the flag in the user's own OpenRGB config so a user-started server works too.
/// </summary>
public static class OpenRgbConfigWriter
{
    public const string ConfigFileName = "OpenRGB.json";

    /// <summary>FANZi's private OpenRGB config folder (%LOCALAPPDATA%\FANZI\OpenRGB\config).</summary>
    public static string PrivateConfigDirectory =>
        Path.Combine(OpenRgbInstallLocator.ManagedInstallDirectory, "config");

    /// <summary>The user's shared OpenRGB config (%APPDATA%\OpenRGB\OpenRGB.json).</summary>
    public static string SharedConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRGB", ConfigFileName);

    private static readonly Regex LegacyOff =
        new(@"(""legacy_workaround""\s*:\s*)false", RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="existingJson"/> with Server.legacy_workaround = true, the SDK server
    /// bound to 127.0.0.1:<paramref name="port"/>, and every other setting preserved. Invalid or
    /// empty input yields a minimal valid config.
    /// </summary>
    public static string ApplyServerSettings(string? existingJson, int port)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(existingJson)
                ? new JsonObject()
                : JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions
                  {
                      AllowTrailingCommas = true,
                      CommentHandling = JsonCommentHandling.Skip,
                  }) as JsonObject ?? new JsonObject();

            if (root["Server"] is not JsonObject server)
            {
                server = new JsonObject();
                root["Server"] = server;
            }

            server["legacy_workaround"] = true;
            server["default_host"] = "127.0.0.1";
            server["default_port"] = port;

            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // OpenRGB sometimes writes duplicate detector keys (JsonObject rejects those lazily with
            // ArgumentException). Fall back to a text patch that touches only the one flag.
            string patched = PatchLegacyFlagText(existingJson ?? "", out bool changed);
            return changed || patched.Contains("\"legacy_workaround\"", StringComparison.Ordinal)
                ? patched
                : new JsonObject
                  {
                      ["Server"] = new JsonObject
                      {
                          ["legacy_workaround"] = true,
                          ["default_host"] = "127.0.0.1",
                          ["default_port"] = port,
                      },
                  }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>Text-level patch: flips an explicit "legacy_workaround": false to true and nothing else.</summary>
    public static string PatchLegacyFlagText(string json, out bool changed)
    {
        string patched = LegacyOff.Replace(json, "${1}true");
        changed = !ReferenceEquals(patched, json) && patched != json;
        return patched;
    }

    /// <summary>Writes FANZi's private config (creating the folder) and returns the folder path.</summary>
    /// <param name="smbusHandsOff">4.5: true turns OpenRGB's SMBus RAM / board detectors off (a vendor app owns that
    /// bus), false turns back on the ones FANZi turned off. Null leaves the detector list untouched.</param>
    public static string EnsurePrivateConfig(int port, bool? smbusHandsOff = null)
    {
        string dir = PrivateConfigDirectory;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, ConfigFileName);
        string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
        string updated = ApplyServerSettings(existing, port);
        if (smbusHandsOff is bool off)
            updated = ApplySmbusDetectors(updated, RgbCoexistence.SmbusDetectors, enabled: !off);
        if (updated != existing)
            File.WriteAllText(path, updated);
        return dir;
    }

    /// <summary>
    /// Sets the given detectors on or off inside <c>Detectors → detectors</c>. Text-level, because OpenRGB writes
    /// duplicate keys that JSON object models reject: an existing <c>"name": true|false</c> is rewritten in place, and
    /// a missing entry is added (creating the section when the file has none). Everything else is preserved. Pure.
    /// </summary>
    public static string ApplySmbusDetectors(string json, System.Collections.Generic.IReadOnlyList<string> detectors, bool enabled)
    {
        string value = enabled ? "true" : "false";
        var missing = new System.Collections.Generic.List<string>();
        foreach (string name in detectors)
        {
            var rx = new Regex("(\"" + Regex.Escape(name) + "\"\\s*:\\s*)(true|false)");
            if (rx.IsMatch(json)) json = rx.Replace(json, "${1}" + value);
            else missing.Add(name);
        }
        // Missing entries only matter when turning detectors off (absent = OpenRGB's default, which is on).
        if (enabled || missing.Count == 0) return json;

        string entries = string.Join(",\n", missing.ConvertAll(n => $"      \"{n}\": false"));
        var section = new Regex("\"detectors\"\\s*:\\s*\\{");
        var m = section.Match(json);
        if (m.Success)
        {
            int insertAt = m.Index + m.Length;
            bool empty = Regex.IsMatch(json.Substring(insertAt), "^\\s*\\}");
            return json.Insert(insertAt, "\n" + entries + (empty ? "\n    " : ","));
        }

        int open = json.IndexOf('{');
        if (open < 0) return "{\n  \"Detectors\": {\n    \"detectors\": {\n" + entries + "\n    }\n  }\n}";
        bool onlyBraces = Regex.IsMatch(json.Substring(open + 1), "^\\s*\\}");
        return json.Insert(open + 1, "\n  \"Detectors\": {\n    \"detectors\": {\n" + entries + "\n    }\n  }" + (onlyBraces ? "\n" : ","));
    }

    /// <summary>
    /// Turns legacy_workaround on in the user's shared OpenRGB config if it is explicitly off.
    /// Only that one value changes (a backup is written first). Returns true when patched.
    /// </summary>
    public static bool TryPatchSharedConfig()
    {
        try
        {
            string path = SharedConfigPath;
            if (!File.Exists(path)) return false;

            string json = File.ReadAllText(path);
            string patched = PatchLegacyFlagText(json, out bool changed);
            if (!changed) return false;

            File.Copy(path, path + ".fanzi-backup", overwrite: true);
            File.WriteAllText(path, patched);
            AppLog.Info($"OpenRGB: enabled Server → legacy_workaround in {path} (backup: {path}.fanzi-backup)");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("OpenRGB: could not patch the shared OpenRGB config", ex);
            return false;
        }
    }
}
