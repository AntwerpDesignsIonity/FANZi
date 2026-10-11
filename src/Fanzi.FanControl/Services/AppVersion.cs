using System;
using System.Reflection;

namespace Fanzi.FanControl.Services;

/// <summary>
/// The app version shown in the UI, logs, crash reports and the update check — read from the
/// assembly (csproj &lt;Version&gt;), so bumping the project version is the only change needed.
/// </summary>
public static class AppVersion
{
    /// <summary>Three-part version, e.g. "2.2.0".</summary>
    public static string Display { get; } = Resolve(typeof(AppVersion).Assembly);

    /// <summary>Major.minor label for compact UI, e.g. "v2.2".</summary>
    public static string ShortLabel { get; } = "v" + ToShort(Display);

    /// <summary>Full product title for the About card, e.g. "FANZi IO-nity v2.2.0".</summary>
    public static string ProductTitle { get; } = $"FANZi IO-nity v{Display}";

    /// <summary>InformationalVersion without the "+commit" suffix, else AssemblyVersion (3 parts).</summary>
    internal static string Resolve(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            string trimmed = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        Version? version = assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(version.Build >= 0 ? 3 : 2);
    }

    /// <summary>"2.2.0" → "2.2"; "2.2.0-beta" → "2.2"; anything without a dot is returned unchanged.</summary>
    internal static string ToShort(string version)
    {
        string core = version.Split('-', 2)[0];
        string[] parts = core.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : core;
    }
}
