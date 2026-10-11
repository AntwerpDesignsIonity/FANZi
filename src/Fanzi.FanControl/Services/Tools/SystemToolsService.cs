using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services.Tools;

/// <summary>One entry of the Tools tab's catalogue: a real Windows command with a short explanation.</summary>
/// <param name="Id">Stable id (tests, AEDi search).</param>
/// <param name="Group">Section: "Repair", "Network", "Fixes".</param>
/// <param name="Icon">Emoji shown on the button.</param>
/// <param name="Title">Button text.</param>
/// <param name="Description">What it does / when to use it (tooltip and help).</param>
/// <param name="Command">The command that runs.</param>
/// <param name="Confirm">Non-null: ask before running (disruptive or needs a restart).</param>
public sealed record ToolAction(string Id, string Group, string Icon, string Title, string Description, ToolCommand Command, string? Confirm = null)
{
    public string Label => $"{Icon} {Title}";
}

/// <summary>A Windows power plan from <c>powercfg /list</c>.</summary>
public sealed record PowerPlan(Guid Id, string Name, bool IsActive)
{
    public string Display => IsActive ? $"● {Name}" : Name;
}

/// <summary>
/// v4.1.0 System tools: repair (SFC, DISM, CHKDSK online scan, component store clean-up, restore point), network
/// fixes (DNS, Winsock, TCP/IP, IP renew, time sync), quick fixes (Explorer, icon cache, audio, Windows Update
/// services) and Power &amp; Gaming (power plans incl. Ultimate Performance, Game Mode, hardware-accelerated GPU
/// scheduling, hibernation). Every action is a real Windows built-in; FANZi only runs and shows it.
/// </summary>
public sealed class SystemToolsService
{
    /// <summary>Windows' hidden Ultimate Performance scheme (powercfg -duplicatescheme source).</summary>
    public static readonly Guid UltimatePerformanceTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameModeValue = "AutoGameModeEnabled";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string HagsValue = "HwSchMode";

    private static readonly Regex PlanPattern = new(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)\s*(\*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string SystemDrive => (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');

    /// <summary>Repair, network and quick-fix actions in display order.</summary>
    public static IReadOnlyList<ToolAction> Catalogue { get; } = BuildCatalogue();

    private static IReadOnlyList<ToolAction> BuildCatalogue()
    {
        string sys = SystemDrive;
        var list = new List<ToolAction>
        {
            // ── Repair ──
            new("restore-point", "Repair", "🛟", "Create restore point",
                "Saves a System Restore point first, so any change can be rolled back (Windows allows one per 24 h by default).",
                new ToolCommand("Create restore point", "powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Enable-ComputerRestore -Drive '" + sys + "\\' -ErrorAction SilentlyContinue; Checkpoint-Computer -Description 'FANZi IO-nity' -RestorePointType MODIFY_SETTINGS; 'Restore point request completed.'\"")),
            new("sfc", "Repair", "🛡", "SFC scan & repair",
                "System File Checker: verifies every protected Windows file and replaces corrupted ones (10–20 min).",
                new ToolCommand("System File Checker", "sfc.exe", "/scannow")),
            new("dism-check", "Repair", "🔎", "DISM check health",
                "Quick check whether the Windows component store (the source SFC repairs from) is flagged as corrupted.",
                new ToolCommand("DISM CheckHealth", "dism.exe", "/Online /Cleanup-Image /CheckHealth")),
            new("dism-scan", "Repair", "🧪", "DISM scan health",
                "Full scan of the component store for corruption (5–15 min). Read-only.",
                new ToolCommand("DISM ScanHealth", "dism.exe", "/Online /Cleanup-Image /ScanHealth")),
            new("dism-restore", "Repair", "🩹", "DISM restore health",
                "Repairs the component store from Windows Update (needs internet, 10–30 min). Run SFC again afterwards.",
                new ToolCommand("DISM RestoreHealth", "dism.exe", "/Online /Cleanup-Image /RestoreHealth")),
            new("winsxs-cleanup", "Repair", "📦", "Component store clean-up",
                "Removes superseded Windows Update components from WinSxS — often frees several GB.",
                new ToolCommand("DISM StartComponentCleanup", "dism.exe", "/Online /Cleanup-Image /StartComponentCleanup")),
            new("chkdsk-scan", "Repair", "💽", $"CHKDSK online scan {sys}",
                "Scans the system volume for file-system errors while Windows runs (no reboot, no changes; spot-fix is scheduled if needed).",
                new ToolCommand($"CHKDSK {sys} /scan", "chkdsk.exe", $"{sys} /scan")),

            // ── Network ──
            new("flush-dns", "Network", "🌐", "Flush DNS",
                "Clears the DNS resolver cache — fixes sites that moved or stale lookups.",
                new ToolCommand("Flush DNS", "ipconfig.exe", "/flushdns")),
            new("renew-ip", "Network", "🔄", "Release & renew IP",
                "Gets a fresh address from the router (DHCP). The connection drops for a few seconds.",
                new ToolCommand("Release & renew IP", "cmd.exe", "/c ipconfig /release && ipconfig /renew"),
                "Your network connection will drop for a few seconds while the IP address is renewed."),
            new("winsock-reset", "Network", "🧦", "Reset Winsock",
                "Resets the Windows socket catalogue — fixes broken connectivity after VPN / proxy / malware clean-ups.",
                new ToolCommand("Reset Winsock", "netsh.exe", "winsock reset", NeedsRestart: true),
                "Winsock reset needs a restart to finish. Continue?"),
            new("tcpip-reset", "Network", "🔌", "Reset TCP/IP stack",
                "Rewrites the TCP/IP registry settings to defaults. Static IP settings are kept per adapter but re-check them.",
                new ToolCommand("Reset TCP/IP", "netsh.exe", "int ip reset", NeedsRestart: true),
                "TCP/IP reset needs a restart to finish. Continue?"),
            new("time-sync", "Network", "🕒", "Sync clock",
                "Forces Windows Time to resync with the internet time server (fixes certificate / login errors from clock drift).",
                new ToolCommand("Sync clock", "cmd.exe", "/c net start w32time >nul 2>&1 & w32tm /resync /force")),

            // ── Quick fixes ──
            new("restart-explorer", "Fixes", "🗂", "Restart Explorer",
                "Restarts the taskbar, Start menu and desktop — fixes a frozen taskbar without logging off.",
                // Windows restarts the shell itself (as the user, not elevated); only if it doesn't within ~3 s is
                // Explorer started with a non-admin token (runas /trustlevel), never elevated from FANZi.
                new ToolCommand("Restart Explorer", "cmd.exe",
                    "/c taskkill /f /im explorer.exe & ping -n 4 127.0.0.1 >nul & (tasklist /fi \"imagename eq explorer.exe\" | find /i \"explorer.exe\" >nul || runas /trustlevel:0x20000 explorer.exe) & echo Explorer restarted."),
                "The taskbar and open Explorer windows will close and reopen."),
            new("icon-cache", "Fixes", "🖼", "Refresh icon cache",
                "Rebuilds the icon / thumbnail cache entries (blank or wrong icons).",
                new ToolCommand("Refresh icon cache", "ie4uinit.exe", "-show")),
            new("restart-audio", "Fixes", "🔊", "Restart audio",
                "Restarts the Windows Audio services — fixes no sound / crackling without a reboot.",
                new ToolCommand("Restart audio", "powershell.exe",
                    "-NoProfile -Command \"Restart-Service -Name AudioEndpointBuilder -Force; Start-Service Audiosrv; 'Audio services restarted.'\"")),
            new("reset-wu", "Fixes", "⬇", "Reset Windows Update",
                "Stops the update services, clears the download cache (SoftwareDistribution\\Download) and starts them again — fixes stuck updates.",
                new ToolCommand("Reset Windows Update", "cmd.exe",
                    "/c net stop wuauserv & net stop bits & rd /s /q \"%windir%\\SoftwareDistribution\\Download\" & net start bits & net start wuauserv"),
                "Pending update downloads will be cleared and downloaded again. Continue?"),
            new("memory-test", "Fixes", "🧠", "Memory diagnostic",
                "Opens Windows Memory Diagnostic to test RAM on the next restart (you choose when).",
                new ToolCommand("Windows Memory Diagnostic", "mdsched.exe", "")),
        };
        return list;
    }

    /// <summary>Parses <c>powercfg /list</c>: every scheme with its name and the active one (marked '*').</summary>
    public static IReadOnlyList<PowerPlan> ParsePowerPlans(string output)
    {
        var plans = new List<PowerPlan>();
        foreach (var line in (output ?? "").Split('\n'))
        {
            var m = PlanPattern.Match(line);
            if (!m.Success || !Guid.TryParse(m.Groups[1].Value, out var id)) continue;
            plans.Add(new PowerPlan(id, m.Groups[2].Value.Trim(), m.Groups[3].Success));
        }
        return plans;
    }

    public static ToolCommand ListPlansCommand => new("Power plans", "powercfg.exe", "/list");
    public static ToolCommand SetActiveCommand(Guid plan) => new("Activate power plan", "powercfg.exe", $"/setactive {plan:D}");
    public static ToolCommand AddUltimateCommand => new("Add Ultimate Performance", "powercfg.exe", $"-duplicatescheme {UltimatePerformanceTemplate:D}");
    public static ToolCommand HibernationCommand(bool on) => new(on ? "Turn hibernation on" : "Turn hibernation off", "powercfg.exe", on ? "/hibernate on" : "/hibernate off");

    /// <summary>True when one of <paramref name="plans"/> is Ultimate Performance (by name; the copy gets a new GUID).</summary>
    public static bool HasUltimate(IEnumerable<PowerPlan> plans)
    {
        foreach (var p in plans)
            if (p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) || p.Id == UltimatePerformanceTemplate) return true;
        return false;
    }

    // ── Gaming toggles (registry) ─────────────────────────────────────────────

    /// <summary>Windows Game Mode: on unless AutoGameModeEnabled = 0 (absent means on in Windows 10 1903+ / 11).</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsGameModeOn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GameBarKey);
            return key?.GetValue(GameModeValue) is not int v || v != 0;
        }
        catch { return true; }
    }

    [SupportedOSPlatform("windows")]
    public static void SetGameMode(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GameBarKey, writable: true);
        key.SetValue(GameModeValue, on ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AllowAutoGameMode", on ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>Hardware-accelerated GPU scheduling: HwSchMode 2 = on, 1 = off, absent = driver default (null).</summary>
    [SupportedOSPlatform("windows")]
    public static bool? IsHagsOn()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKey);
            return key?.GetValue(HagsValue) is int v ? v == 2 : null;
        }
        catch { return null; }
    }

    /// <summary>Sets HAGS (needs Administrator; takes effect after a restart).</summary>
    [SupportedOSPlatform("windows")]
    public static void SetHags(bool on)
    {
        using var key = Registry.LocalMachine.CreateSubKey(GraphicsDriversKey, writable: true);
        key.SetValue(HagsValue, on ? 2 : 1, RegistryValueKind.DWord);
    }

    /// <summary>Size of hiberfil.sys on the system drive, or null when hibernation is off / the file can't be read.</summary>
    public static long? HiberfilBytes()
    {
        try
        {
            var file = new FileInfo(Path.Combine(SystemDrive + "\\", "hiberfil.sys"));
            return file.Exists ? file.Length : null;
        }
        catch { return null; }
    }

    /// <summary>Runs <c>powercfg /list</c> and parses it.</summary>
    public static async Task<IReadOnlyList<PowerPlan>> ListPowerPlansAsync(ToolProcessRunner runner, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(ListPlansCommand, null, ct).ConfigureAwait(false);
        return ParsePowerPlans(result.Output);
    }
}
