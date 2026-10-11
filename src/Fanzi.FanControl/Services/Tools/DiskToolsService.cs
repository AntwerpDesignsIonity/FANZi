using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services.Tools;

/// <summary>Storage media behind a volume (from Windows Storage Management: Get-PhysicalDisk).</summary>
public enum DiskMedia { Unknown, Ssd, Hdd }

/// <summary>A fixed volume with the physical disk it lives on.</summary>
public sealed record DiskVolume(char Letter, string Label, string FileSystem, long SizeBytes, long FreeBytes,
    int? DiskNumber, DiskMedia Media, string Bus, string DiskName)
{
    public string Root => $"{Letter}:";
    public double UsedPercent => SizeBytes > 0 ? Math.Clamp(100.0 * (SizeBytes - FreeBytes) / SizeBytes, 0, 100) : 0;
    public string MediaText => Media switch { DiskMedia.Ssd => "SSD", DiskMedia.Hdd => "HDD", _ => "Disk" };
    public bool CanDefragment => Media != DiskMedia.Ssd;
    public bool CanRetrim => Media != DiskMedia.Hdd;
}

/// <summary>Health of one physical disk (S.M.A.R.T. via Get-StorageReliabilityCounter).</summary>
public sealed record DriveHealth(string Id, string Name, DiskMedia Media, string Bus, string HealthStatus, string OperationalStatus,
    long SizeBytes, double? TemperatureC, double? TemperatureMaxC, double? WearPercent, long? PowerOnHours,
    long? ReadErrors, long? WriteErrors, string? Note)
{
    public bool IsHealthy => string.Equals(HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Healthy" / "Warning" / "Unhealthy" plus the reasons that matter (wear, errors, heat).</summary>
    public string Verdict
    {
        get
        {
            var issues = new List<string>();
            if (!IsHealthy && !string.IsNullOrWhiteSpace(HealthStatus)) issues.Add($"Windows reports {HealthStatus}");
            if (WearPercent is >= 80) issues.Add($"{WearPercent:F0} % worn");
            if (ReadErrors is > 0 || WriteErrors is > 0) issues.Add("read/write errors logged");
            if (TemperatureC is >= 65) issues.Add($"hot ({TemperatureC:F0} °C)");
            return issues.Count == 0 ? "Healthy" : string.Join(" · ", issues);
        }
    }
}

/// <summary>What <c>defrag /A</c> reported.</summary>
public sealed record DefragReport(double? FragmentedPercent, string? Recommendation);

/// <summary>Kinds of optimisation the Windows storage optimiser (defrag.exe) can run.</summary>
public enum DiskOperation { Analyze, Optimize, Defragment, Retrim, BootOptimize }

/// <summary>
/// v4.1.0 Disk Optimizer + Drive Health: lists volumes with their media type (SSD / HDD), builds the real
/// <c>defrag.exe</c> commands (Windows picks defrag for HDD and TRIM for SSD with /O), parses the analysis report,
/// and reads drive health / S.M.A.R.T. counters through Windows Storage Management.
/// </summary>
public sealed class DiskToolsService
{
    private static readonly Regex FragmentedPattern = new(@"Total fragmented space\s*=\s*(\d{1,3})\s*%", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RecommendPattern = new(@"^.*(do not need to (defragment|optimi[sz]e)|recommended that you (defragment|optimi[sz]e)|should (defragment|optimi[sz]e)).*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The defrag.exe command for <paramref name="operation"/> on <paramref name="letter"/> (with progress /U and verbose /V).</summary>
    public static ToolCommand CommandFor(DiskOperation operation, char letter)
    {
        char l = char.ToUpperInvariant(letter);
        if (l < 'A' || l > 'Z') throw new ArgumentOutOfRangeException(nameof(letter), letter, "Drive letter A-Z expected.");
        return operation switch
        {
            DiskOperation.Analyze => new ToolCommand($"Analyse {l}:", "defrag.exe", $"{l}: /A /U /V"),
            DiskOperation.Optimize => new ToolCommand($"Optimise {l}:", "defrag.exe", $"{l}: /O /U /V"),
            DiskOperation.Defragment => new ToolCommand($"Defragment {l}:", "defrag.exe", $"{l}: /D /U /V"),
            DiskOperation.Retrim => new ToolCommand($"ReTrim {l}:", "defrag.exe", $"{l}: /L /U /V"),
            DiskOperation.BootOptimize => new ToolCommand($"Boot optimise {l}:", "defrag.exe", $"{l}: /B /U /V"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    /// <summary>Fragmentation and recommendation from a defrag analysis log.</summary>
    public static DefragReport ParseReport(string output)
    {
        double? fragmented = null;
        var m = FragmentedPattern.Match(output ?? "");
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            fragmented = v;
        var r = RecommendPattern.Match(output ?? "");
        return new DefragReport(fragmented, r.Success ? r.Value.Trim() : null);
    }

    internal static DiskMedia ParseMedia(string? media) => (media ?? "").Trim().ToUpperInvariant() switch
    {
        "SSD" or "4" => DiskMedia.Ssd,
        "HDD" or "3" => DiskMedia.Hdd,
        _ => DiskMedia.Unknown,
    };

    private const string VolumeScript =
        "$ErrorActionPreference='SilentlyContinue';" +
        "$pd=@{}; Get-PhysicalDisk | % { $pd[[string]$_.DeviceId]=$_ };" +
        "$o=Get-Volume | ? { $_.DriveLetter -and $_.DriveType -eq 'Fixed' } | % { $v=$_;" +
        " $p=Get-Partition -DriveLetter $v.DriveLetter | Select -First 1; $d=$null; if($p){ $d=$pd[[string]$p.DiskNumber] };" +
        " [pscustomobject]@{L=[string]$v.DriveLetter; Label=$v.FileSystemLabel; Fs=$v.FileSystem; Size=[int64]$v.Size; Free=[int64]$v.SizeRemaining;" +
        " Disk=$(if($p){[int]$p.DiskNumber}else{$null}); Media=$(if($d){[string]$d.MediaType}else{''}); Bus=$(if($d){[string]$d.BusType}else{''}); Name=$(if($d){$d.FriendlyName}else{''}) } };" +
        "ConvertTo-Json @($o) -Compress";

    private const string HealthScript =
        "$ErrorActionPreference='SilentlyContinue';" +
        "$o=Get-PhysicalDisk | % { $r=$null; $err=$null; try { $r = $_ | Get-StorageReliabilityCounter -ErrorAction Stop } catch { $err=$_.Exception.Message };" +
        " [pscustomobject]@{Id=[string]$_.DeviceId; Name=$_.FriendlyName; Media=[string]$_.MediaType; Bus=[string]$_.BusType; Health=[string]$_.HealthStatus;" +
        " Op=($_.OperationalStatus -join ', '); Size=[int64]$_.Size; Temp=$r.Temperature; TempMax=$r.TemperatureMax; Wear=$r.Wear;" +
        " ReadErr=$r.ReadErrorsTotal; WriteErr=$r.WriteErrorsTotal; Hours=$r.PowerOnHours; Err=$err } };" +
        "ConvertTo-Json @($o) -Compress";

    /// <summary>Fixed volumes with their disk's media type; falls back to <see cref="DriveInfo"/> when Storage Management is unavailable.</summary>
    public async Task<IReadOnlyList<DiskVolume>> ListVolumesAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await RunPowerShellAsync(VolumeScript, ct).ConfigureAwait(false);
            var volumes = ParseVolumes(json);
            if (volumes.Count > 0) return volumes;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Warn("Listing volumes via Storage Management failed — using DriveInfo", ex); }

        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.Name.Length >= 1)
            .Select(d => new DiskVolume(char.ToUpperInvariant(d.Name[0]), d.VolumeLabel, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace,
                null, DiskMedia.Unknown, "", ""))
            .OrderBy(v => v.Letter)
            .ToList();
    }

    /// <summary>Physical disks with health and reliability counters (counters need Administrator).</summary>
    public async Task<IReadOnlyList<DriveHealth>> ListDriveHealthAsync(CancellationToken ct = default)
    {
        string json = await RunPowerShellAsync(HealthScript, ct).ConfigureAwait(false);
        return ParseHealth(json);
    }

    internal static IReadOnlyList<DiskVolume> ParseVolumes(string json)
    {
        var list = new List<DiskVolume>();
        foreach (var e in JsonItems(json))
        {
            string letter = Str(e, "L");
            if (letter.Length == 0 || !char.IsLetter(letter[0])) continue;
            list.Add(new DiskVolume(char.ToUpperInvariant(letter[0]), Str(e, "Label"), Str(e, "Fs"), Long(e, "Size") ?? 0, Long(e, "Free") ?? 0,
                (int?)Long(e, "Disk"), ParseMedia(Str(e, "Media")), Str(e, "Bus"), Str(e, "Name")));
        }
        return list.OrderBy(v => v.Letter).ToList();
    }

    internal static IReadOnlyList<DriveHealth> ParseHealth(string json)
    {
        var list = new List<DriveHealth>();
        foreach (var e in JsonItems(json))
        {
            string? err = Str(e, "Err") is { Length: > 0 } s ? s : null;
            double? temp = Dbl(e, "Temp");
            if (temp is <= 0) temp = null; // 0 = not reported
            double? tempMax = Dbl(e, "TempMax");
            if (tempMax is <= 0) tempMax = null;
            string? note = err is not null
                ? (err.Contains("not available", StringComparison.OrdinalIgnoreCase) || err.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    ? "S.M.A.R.T. counters need FANZi running as Administrator."
                    : err)
                : null;
            list.Add(new DriveHealth(Str(e, "Id"), Str(e, "Name"), ParseMedia(Str(e, "Media")), Str(e, "Bus"), Str(e, "Health"), Str(e, "Op"),
                Long(e, "Size") ?? 0, temp, tempMax, Dbl(e, "Wear"), Long(e, "Hours"), Long(e, "ReadErr"), Long(e, "WriteErr"), note));
        }
        return list.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<JsonElement> JsonItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object) yield return e.Clone();
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root.Clone();
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? "",
            JsonValueKind.Number => p.GetRawText(),
            _ => "",
        } : "";

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var l)) return l;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return (long)d;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static double? Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    /// <summary>Runs a short PowerShell script hidden and returns its standard output.</summary>
    internal static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                     "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe could not start.");
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException("PowerShell storage query timed out.");
        }
        ct.ThrowIfCancellationRequested();
        string output = (await stdout.ConfigureAwait(false)).Trim();
        string err = (await stderr.ConfigureAwait(false)).Trim();
        if (output.Length == 0 && err.Length > 0) throw new InvalidOperationException(err);
        return output;
    }
}
