using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services;

public enum DoctorStatus { Ok, Fixed, Attention, Skipped }

/// <summary>Result of one Self-Doctor check.</summary>
public sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail)
{
    public string Icon => Status switch { DoctorStatus.Ok => "✓", DoctorStatus.Fixed => "⚕", DoctorStatus.Attention => "!", _ => "–" };
    public string Color => Status switch { DoctorStatus.Ok => "#34D399", DoctorStatus.Fixed => "#60A5FA", DoctorStatus.Attention => "#FBBF24", _ => "#5A7FA8" };
}

/// <summary>What the Self-Doctor can observe and repair. Every member is optional; a missing probe skips its check.</summary>
public sealed class SelfDoctorContext
{
    public Func<HardwareSnapshot?>? LastSnapshot { get; init; }
    public Func<string?>? SensorInitError { get; init; }
    public Func<Task<bool>>? RecoverSensors { get; init; }
    public Func<bool>? RgbWanted { get; init; }
    public Func<bool>? RgbConnected { get; init; }
    public Func<Task>? RepairRgb { get; init; }
    public Func<bool>? MaxsunExpected { get; init; }
    public Func<bool>? MaxsunOnline { get; init; }
    public Func<string?>? MaxsunServiceState { get; init; }
    public Func<bool>? StartMaxsunService { get; init; }
    public Func<long>? WorkingSetBytes { get; init; }
    public Func<TimeSpan, Task<bool>>? UiResponds { get; init; }
    public Func<string?>? ExtraReport { get; init; }
}

/// <summary>
/// 4.4 AEDi Self-Doctor — FANZi's auto-debugger. Every 30 s it checks FANZi's own moving parts, repairs what it safely
/// can and keeps evidence for what it cannot:
/// <list type="bullet">
/// <item>Sensors: dashboard refresh stalled, or the CPU stopped reporting temperatures → the sensor driver is reopened
/// (fan headers are handed back to the BIOS first).</item>
/// <item>Fans: a fan that was spinning this session now reads 0 RPM while it is being driven → stall / unplugged.</item>
/// <item>RGB: lighting on but OpenRGB disconnected → reconnect. MAXSUN Sync service stopped → started again.</item>
/// <item>Memory: working set above 1.5 GB → compacting GC; above 2.5 GB after that → flagged.</item>
/// <item>UI: the window thread did not answer within 5 s → logged as a hang.</item>
/// <item>Error storms: the same warning / error ≥ 15 times in 10 min → flagged, and a diagnostics bundle (logs, crash
/// logs, redacted settings, the Self-Doctor report) is written to Documents\FANZi\Diagnostics automatically.</item>
/// </list>
/// Each repair runs at most once per 5 minutes so a fault the doctor cannot cure never turns into a repair loop.
/// </summary>
public sealed class SelfDoctor : IDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RepairCooldown = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan StaleSnapshot = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan StormWindow = TimeSpan.FromMinutes(10);
    internal const int StormThreshold = 15;
    internal const long MemoryCompactBytes = 1_500L * 1024 * 1024;
    internal const long MemoryAlarmBytes = 2_500L * 1024 * 1024;
    internal const int MissingTempChecks = 2;
    private const string LogPrefix = "Self-Doctor";

    private readonly SelfDoctorContext _ctx;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRepair = new();
    private readonly Queue<(DateTimeOffset At, string Signature)> _errors = new();
    private readonly HashSet<string> _fansSeenSpinning = new(StringComparer.OrdinalIgnoreCase);
    private int _missingTempStreak;
    private int _rgbDownStreak;
    private DateTimeOffset _lastBundle = DateTimeOffset.MinValue;
    private CancellationTokenSource? _loop;
    private int _running;
    private string _lastSummary = "";

    public SelfDoctor(SelfDoctorContext context, Func<DateTimeOffset>? clock = null)
    {
        _ctx = context;
        _clock = clock ?? (() => DateTimeOffset.Now);
        AppLog.Logged += OnLogged;
    }

    /// <summary>Repairs that worked since start.</summary>
    public int FixesApplied { get; private set; }

    public IReadOnlyList<DoctorCheck> LastChecks { get; private set; } = Array.Empty<DoctorCheck>();
    public DateTimeOffset? LastRun { get; private set; }
    public string? LastBundlePath { get; private set; }

    /// <summary>Raised on a pool thread after every pass.</summary>
    public event Action<IReadOnlyList<DoctorCheck>>? Completed;

    /// <summary>Raised when a check newly needs the user's attention (title, message).</summary>
    public event Action<string, string>? NeedsAttention;

    public void Start()
    {
        if (_loop is not null) return;
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), token); } catch (OperationCanceledException) { return; }
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try { await RunAsync(token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { AppLog.Warn($"{LogPrefix}: pass failed", ex); }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token);
        AppLog.Info($"{LogPrefix}: started (checks every {Interval.TotalSeconds:F0} s).");
    }

    /// <summary>Runs every check once (also the "Run checks now" button). Overlapping calls return the last result.</summary>
    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return LastChecks;
        try
        {
            var now = _clock();
            var checks = new List<DoctorCheck>
            {
                await CheckSensorsAsync(now).ConfigureAwait(false),
                CheckFans(),
                await CheckRgbAsync(now).ConfigureAwait(false),
                CheckMaxsun(now),
                CheckMemory(now),
                await CheckUiAsync().ConfigureAwait(false),
                CheckErrorStorm(now),
            };
            checks.RemoveAll(c => c.Status == DoctorStatus.Skipped);
            cancellationToken.ThrowIfCancellationRequested();

            var previous = LastChecks;
            LastChecks = checks;
            LastRun = now;
            FixesApplied += checks.Count(c => c.Status == DoctorStatus.Fixed);

            string summary = string.Join(" | ", checks.Where(c => c.Status != DoctorStatus.Ok).Select(c => $"{c.Name}: {c.Status}"));
            if (summary != _lastSummary)
            {
                _lastSummary = summary;
                AppLog.Info($"{LogPrefix}: " + (summary.Length == 0 ? "all checks OK" : summary));
            }

            foreach (var c in checks.Where(c => c.Status == DoctorStatus.Attention))
                if (!previous.Any(p => p.Name == c.Name && p.Status == DoctorStatus.Attention))
                    NeedsAttention?.Invoke($"Self-Doctor: {c.Name}", c.Detail);

            Completed?.Invoke(checks);
            return checks;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    // ── Checks ────────────────────────────────────────────────────────────────

    private async Task<DoctorCheck> CheckSensorsAsync(DateTimeOffset now)
    {
        const string name = "Sensors";
        if (_ctx.LastSnapshot is null) return new(name, DoctorStatus.Skipped, "");
        string? initError = _ctx.SensorInitError?.Invoke();
        var s = _ctx.LastSnapshot();
        if (s is null)
            return new(name, initError is null ? DoctorStatus.Ok : DoctorStatus.Attention, initError ?? "Waiting for the first reading.");

        if (now - s.Timestamp > StaleSnapshot)
            return new(name, DoctorStatus.Attention,
                $"Last reading is {(now - s.Timestamp).TotalSeconds:F0} s old — the refresh loop is stalled. FANZi logged this for the developers; restart FANZi if it stays.");

        bool noTemp = s.CpuPackageTemperature is null && s.CpuAverageTemperature is null && s.CpuHotspotTemperature is null;
        _missingTempStreak = noTemp ? _missingTempStreak + 1 : 0;
        if (_missingTempStreak < MissingTempChecks)
            return new(name, DoctorStatus.Ok, noTemp ? "CPU temperature missing in one reading — watching." : "Live — CPU and GPU sensors answer.");

        if (_ctx.RecoverSensors is null || !TryClaimRepair(name, now))
            return new(name, DoctorStatus.Attention, "The CPU stopped reporting temperatures. Run FANZi as Administrator and close other tools that hold the sensor driver.");

        AppLog.Warn($"{LogPrefix}: CPU temperatures missing for {_missingTempStreak} checks — reopening the sensor driver");
        bool ok = false;
        try { ok = await _ctx.RecoverSensors().ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn($"{LogPrefix}: sensor recovery failed", ex); }
        if (ok) _missingTempStreak = 0;
        return ok
            ? new(name, DoctorStatus.Fixed, "CPU temperatures had stopped — the sensor driver was reopened (fan headers were handed back to the BIOS first).")
            : new(name, DoctorStatus.Attention, "CPU temperatures stopped and reopening the sensor driver did not help. Another monitoring tool may hold the driver.");
    }

    private DoctorCheck CheckFans()
    {
        const string name = "Fans";
        var s = _ctx.LastSnapshot?.Invoke();
        if (s is null || s.Fans.Count == 0) return new(name, DoctorStatus.Skipped, "");
        var stalled = new List<string>();
        foreach (var f in s.Fans)
        {
            if (f.SpeedRpm is > 300) { _fansSeenSpinning.Add(f.Id); continue; }
            if (f.SpeedRpm is double rpm && rpm < 50 && _fansSeenSpinning.Contains(f.Id) && f.CurrentControlPercent is >= 35)
                stalled.Add($"{f.Name} (0 RPM at {f.CurrentControlPercent:F0}%)");
        }
        return stalled.Count == 0
            ? new(name, DoctorStatus.Ok, $"{_fansSeenSpinning.Count} fan(s) spinning normally.")
            : new(name, DoctorStatus.Attention, $"Stopped while driven: {string.Join(", ", stalled)}. It was spinning earlier — check the cable, a blocked blade or a failed fan.");
    }

    private async Task<DoctorCheck> CheckRgbAsync(DateTimeOffset now)
    {
        const string name = "RGB link";
        if (_ctx.RgbWanted is null || _ctx.RgbConnected is null) return new(name, DoctorStatus.Skipped, "");
        if (!_ctx.RgbWanted()) { _rgbDownStreak = 0; return new(name, DoctorStatus.Skipped, ""); }
        if (_ctx.RgbConnected()) { _rgbDownStreak = 0; return new(name, DoctorStatus.Ok, "Lighting connected."); }

        // The RGB tab's own watchdog reconnects within ~15 s; step in only when it has not.
        if (++_rgbDownStreak < 2 || _ctx.RepairRgb is null || !TryClaimRepair(name, now))
            return new(name, DoctorStatus.Attention, "Lighting is on but not connected — FANZi is reconnecting.");

        AppLog.Warn($"{LogPrefix}: RGB disconnected for {_rgbDownStreak} checks — reconnecting");
        try { await _ctx.RepairRgb().ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn($"{LogPrefix}: RGB repair failed", ex); }
        bool ok = _ctx.RgbConnected();
        if (ok) _rgbDownStreak = 0;
        return ok
            ? new(name, DoctorStatus.Fixed, "Lighting had disconnected — reconnected.")
            : new(name, DoctorStatus.Attention, "Lighting is still disconnected after a reconnect. Open the RGB tab → Copy diagnostics.");
    }

    private DoctorCheck CheckMaxsun(DateTimeOffset now)
    {
        const string name = "MAXSUN Sync";
        if (_ctx.MaxsunServiceState is null) return new(name, DoctorStatus.Skipped, "");
        string? state = _ctx.MaxsunServiceState();
        if (state is null) return new(name, DoctorStatus.Skipped, ""); // not installed: nothing to look after
        bool online = _ctx.MaxsunOnline?.Invoke() == true;
        if (state.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
            return new(name, DoctorStatus.Ok, online ? "Service running — RAM and board LEDs go through it." : "Service running — FANZi picks it up on the next RGB rescan.");

        if (_ctx.StartMaxsunService is null || !TryClaimRepair(name, now))
            return new(name, DoctorStatus.Attention, $"MaxsunSyncService is {state.ToLowerInvariant()} — RAM lighting falls back to OpenRGB.");

        AppLog.Warn($"{LogPrefix}: MaxsunSyncService is {state} — starting it");
        bool ok = false;
        try { ok = _ctx.StartMaxsunService(); } catch (Exception ex) { AppLog.Warn($"{LogPrefix}: service start failed", ex); }
        return ok
            ? new(name, DoctorStatus.Fixed, "MaxsunSyncService had stopped — started again; RAM lighting returns within 20 s.")
            : new(name, DoctorStatus.Attention, $"MaxsunSyncService is {state.ToLowerInvariant()} and could not be started.");
    }

    private DoctorCheck CheckMemory(DateTimeOffset now)
    {
        const string name = "Memory";
        long ws = _ctx.WorkingSetBytes?.Invoke() ?? Environment.WorkingSet;
        string mb(long b) => (b / 1048576.0).ToString("F0", CultureInfo.InvariantCulture) + " MB";
        if (ws < MemoryCompactBytes) return new(name, DoctorStatus.Ok, $"{mb(ws)} in use.");
        if (!TryClaimRepair(name, now))
            return new(name, ws >= MemoryAlarmBytes ? DoctorStatus.Attention : DoctorStatus.Ok, $"{mb(ws)} in use (compacted recently).");

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long after = _ctx.WorkingSetBytes?.Invoke() ?? Environment.WorkingSet;
        AppLog.Info($"{LogPrefix}: memory compacted {mb(ws)} → {mb(after)}");
        return after >= MemoryAlarmBytes
            ? new(name, DoctorStatus.Attention, $"{mb(after)} in use even after compacting — a leak; a diagnostics bundle helps the developers.")
            : new(name, DoctorStatus.Fixed, $"Memory was {mb(ws)} — compacted to {mb(after)}.");
    }

    private async Task<DoctorCheck> CheckUiAsync()
    {
        const string name = "Responsiveness";
        if (_ctx.UiResponds is null) return new(name, DoctorStatus.Skipped, "");
        var sw = Stopwatch.StartNew();
        bool ok;
        try { ok = await _ctx.UiResponds(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { ok = false; }
        if (ok) return new(name, DoctorStatus.Ok, $"Window answers in {sw.ElapsedMilliseconds} ms.");
        AppLog.Warn($"{LogPrefix}: window thread did not answer within 5 s (UI hang)");
        return new(name, DoctorStatus.Attention, "The window stopped answering for 5 s or more — logged with timings for the developers.");
    }

    private DoctorCheck CheckErrorStorm(DateTimeOffset now)
    {
        const string name = "Error log";
        string worstSig = "";
        int worstCount = 0, total;
        lock (_lock)
        {
            while (_errors.Count > 0 && now - _errors.Peek().At > StormWindow) _errors.Dequeue();
            total = _errors.Count;
            foreach (var g in _errors.GroupBy(e => e.Signature))
            {
                int c = g.Count();
                if (c > worstCount) { worstCount = c; worstSig = g.Key; }
            }
        }
        if (worstCount < StormThreshold)
            return new(name, DoctorStatus.Ok, total == 0 ? "No warnings in the last 10 minutes." : $"{total} warning(s) in the last 10 minutes — normal.");

        string bundleNote = "";
        if (now - _lastBundle > TimeSpan.FromHours(1))
        {
            _lastBundle = now;
            try
            {
                LastBundlePath = SaveDiagnosticsBundle($"error storm: {worstSig}");
                bundleNote = $" Diagnostics saved: {LastBundlePath}";
            }
            catch (Exception ex) { AppLog.Warn($"{LogPrefix}: diagnostics bundle failed", ex); }
        }
        return new(name, DoctorStatus.Attention, $"Repeating {worstCount}× in 10 min: \"{worstSig}\".{bundleNote}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool TryClaimRepair(string key, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_lastRepair.TryGetValue(key, out var last) && now - last < RepairCooldown) return false;
            _lastRepair[key] = now;
            return true;
        }
    }

    private void OnLogged(string level, string message)
    {
        if (level is not ("WARN" or "ERROR") || message.StartsWith(LogPrefix, StringComparison.Ordinal)) return;
        lock (_lock)
        {
            _errors.Enqueue((_clock(), Signature(message)));
            while (_errors.Count > 2000) _errors.Dequeue();
        }
    }

    /// <summary>Message with numbers stripped, first 90 chars: "frame 1234 failed" and "frame 1240 failed" are one fault.</summary>
    internal static string Signature(string message)
    {
        string s = Regex.Replace(message, @"\d+(\.\d+)?", "#");
        return s.Length > 90 ? s[..90] : s;
    }

    /// <summary>Redacts emails, tokens, passwords and secrets from a settings JSON before it leaves the PC.</summary>
    internal static string Redact(string json) =>
        Regex.Replace(json, "(\"[^\"]*(?:email|token|password|secret|refresh|apikey|api_key|smtp)[^\"]*\"\\s*:\\s*)\"[^\"]*\"",
            "$1\"***\"", RegexOptions.IgnoreCase);

    public static string DiagnosticsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FANZi", "Diagnostics");

    /// <summary>Zips logs, crash logs, redacted settings and the Self-Doctor report; returns the zip path.</summary>
    public string SaveDiagnosticsBundle(string reason)
    {
        Directory.CreateDirectory(DiagnosticsDirectory);
        string path = Path.Combine(DiagnosticsDirectory, $"FANZi-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FANZI");

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddText(zip, "self-doctor.txt", BuildReport(reason));
            foreach (var f in SafeFiles(Path.Combine(appData, "logs"), "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
                AddFile(zip, f, "logs/" + Path.GetFileName(f));
            foreach (var f in SafeFiles(Path.Combine(appData, "crash-logs"), "*").OrderByDescending(File.GetLastWriteTimeUtc).Take(10))
                AddFile(zip, f, "crash-logs/" + Path.GetFileName(f));
            string settings = Path.Combine(appData, "settings.json");
            if (File.Exists(settings))
            {
                try { AddText(zip, "settings.redacted.json", Redact(ReadShared(settings))); } catch { }
            }
            string fp = Path.Combine(appData, "ai", "cooling-fingerprint.json");
            if (File.Exists(fp)) AddFile(zip, fp, "ai/cooling-fingerprint.json");
        }
        AppLog.Info($"{LogPrefix}: diagnostics bundle saved to {path} ({reason})");
        return path;
    }

    private string BuildReport(string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FANZi IO-nity {AppVersion.Display} — Self-Doctor report");
        sb.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}   Reason: {reason}");
        sb.AppendLine($"OS: {Environment.OSVersion}   64-bit: {Environment.Is64BitProcess}   CPUs: {Environment.ProcessorCount}");
        sb.AppendLine($"Working set: {Environment.WorkingSet / 1048576} MB   GC heap: {GC.GetTotalMemory(false) / 1048576} MB   Uptime: {DateTime.Now - Process.GetCurrentProcess().StartTime:hh\\:mm\\:ss}");
        sb.AppendLine($"Fixes applied this session: {FixesApplied}");
        sb.AppendLine();
        foreach (var c in LastChecks) sb.AppendLine($"[{c.Status}] {c.Name}: {c.Detail}");
        sb.AppendLine();
        lock (_lock)
        {
            sb.AppendLine("Top warnings (10 min):");
            foreach (var g in _errors.GroupBy(e => e.Signature).OrderByDescending(g => g.Count()).Take(10))
                sb.AppendLine($"  {g.Count(),4}×  {g.Key}");
        }
        try { if (_ctx.ExtraReport?.Invoke() is { Length: > 0 } extra) sb.AppendLine().AppendLine(extra); } catch { }
        return sb.ToString();
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static void AddFile(ZipArchive zip, string file, string name)
    {
        try { AddText(zip, name, ReadShared(file)); }
        catch (Exception ex) { AddText(zip, name + ".error.txt", ex.Message); }
    }

    public void Dispose()
    {
        AppLog.Logged -= OnLogged;
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }
}
