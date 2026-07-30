using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

/// <summary>
/// AEDi — Antwerp Ecosystems Designs Ionity — Knowledge-Base Search &amp; Ping Engine.
/// Not a chatbot. A directed search AI that maps user queries to application sections,
/// provides instant guidance, and navigates the user to exactly where they need to be.
/// </summary>
public sealed class AediKnowledgeEngine
{
    private readonly List<AediEntry> _entries = new();

    public IReadOnlyList<AediEntry> Entries => _entries;

    public AediKnowledgeEngine()
    {
        BuildKnowledgeBase();
    }

    public IReadOnlyList<AediSearchResult> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<AediSearchResult>();

        string q = query.ToLowerInvariant().Trim();
        string[] terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var results = new List<AediSearchResult>();

        foreach (var entry in _entries)
        {
            int score = 0;

            // Exact title match
            if (entry.Title.ToLowerInvariant().Contains(q))
                score += 50;

            // Keyword matches
            foreach (var keyword in entry.Keywords)
            {
                if (keyword.Contains(q)) score += 30;
                foreach (var term in terms)
                {
                    if (keyword.Contains(term)) score += 10;
                    if (term.Contains(keyword)) score += 5;
                }
            }

            // Description match
            if (entry.Description.ToLowerInvariant().Contains(q))
                score += 20;
            foreach (var term in terms)
            {
                if (entry.Description.ToLowerInvariant().Contains(term))
                    score += 5;
            }

            // Section match
            if (entry.Section.ToLowerInvariant().Contains(q))
                score += 15;

            if (score > 0)
                results.Add(new AediSearchResult(entry, score));
        }

        return results.OrderByDescending(r => r.Score).Take(8).ToList();
    }

    public AediSearchResult? Ping(string quickCommand)
    {
        string cmd = quickCommand.ToLowerInvariant().Trim();

        // Direct navigation commands
        var direct = cmd switch
        {
            "dashboard" or "home" or "main" => "Dashboard",
            "fans" or "fan" or "speed" or "rpm" => "Fans",
            "sensors" or "temp" or "temperature" or "cpu" or "gpu" => "Sensors",
            "rgb" or "light" or "color" or "led" => "RGB Control",
            "tasks" or "process" or "kill" or "end task" or "task manager" => "Tasks",
            "network" or "net" or "bandwidth" or "download" or "upload" or "port" => "Network",
            "power" or "watt" or "energy" or "drive" => "Power",
            "clean" or "cleaner" or "cache" or "temp" or "ram" or "dns" => "System Cleaner",
            "alert" or "warning" or "email" or "notify" => "Alerts",
            "settings" or "config" or "option" => "Settings",
            "health" or "score" or "grade" => "Dashboard",
            "game" or "gaming" => "Dashboard",
            "overlay" or "mini" or "small" => "Overlay",
            "about" or "ionity" or "aedi" or "who" => "About",
            "help" or "?" or "how" => "Help",
            _ => null
        };

        if (direct is not null)
        {
            var entry = _entries.FirstOrDefault(e => e.Section == direct);
            if (entry is not null)
                return new AediSearchResult(entry, 100);
        }

        return null;
    }

    private void BuildKnowledgeBase()
    {
        // Dashboard
        _entries.Add(new AediEntry("Dashboard", "Main overview",
            "System health score, CPU/GPU temperatures, fan speeds, AI status, game mode detection",
            new[] { "dashboard", "home", "overview", "main", "health", "score", "game", "gaming", "status", "temp", "cpu", "gpu", "fan" },
            0,
            "Navigate to Dashboard"));

        _entries.Add(new AediEntry("Health Score", "IO-nity System Health",
            "Real-time 0-100 health grade (A+ to Critical). Analyzes thermal efficiency, dust buildup, fan health, stability.",
            new[] { "health", "score", "grade", "a+", "critical", "efficiency", "dust", "stability" },
            0,
            "Check health score on Dashboard"));

        _entries.Add(new AediEntry("Game Mode", "Auto game detection",
            "Detects 100+ games automatically. Suggests optimal fan profiles and RGB presets for gaming.",
            new[] { "game", "gaming", "fortnite", "valorant", "csgo", "game mode", "fps", "competitive" },
            0,
            "Game Mode on Dashboard"));

        // Fans
        _entries.Add(new AediEntry("Fan Control", "Manual & AI fan speed",
            "Set fan speeds per channel. AI auto-fan with 9 presets: Silent, Balanced, Performance, Gaming, Streaming, Workstation, Overclock, Zero-RPM, Linear.",
            new[] { "fan", "fans", "speed", "rpm", "pwm", "control", "manual", "auto", "preset", "silent", "balanced", "performance", "curve" },
            1,
            "Go to Fans tab"));

        _entries.Add(new AediEntry("Fan Curves", "Temperature-to-speed mapping",
            "Custom fan curves with temperature points. Presets for different use cases. AI learns your thermal profile.",
            new[] { "curve", "fan curve", "temperature", "points", "custom", "preset", "thermal" },
            1, "Fan Curves in Fans tab"));

        _entries.Add(new AediEntry("AEDi AI Engine", "Antwerp Ecosystems Designs Ionity",
            "The AEDi AI engine powers thermal prediction, anomaly detection, acoustic smoothing, game detection, and health scoring. Built by Ionity Global.",
            new[] { "aedi", "ai", "engine", "ionity", "antwerp", "ecosystems", "designs", "intelligence", "prediction", "anomaly" },
            0, "AEDi status on Dashboard"));

        // Sensors
        _entries.Add(new AediEntry("Sensors", "Hardware sensor readings",
            "CPU package/avg/hotspot temps, GPU temps, voltages, clocks, power draw, per-core readings. All from LibreHardwareMonitor.",
            new[] { "sensor", "sensors", "temperature", "temp", "voltage", "clock", "power", "watt", "core", "package", "hotspot", "cpu", "gpu" },
            2, "Go to Sensors tab"));

        // RGB
        _entries.Add(new AediEntry("RGB Control", "Lighting effects & themes",
            "9 effects: Static, Pulse, Rainbow, ColorWave, TemperatureReactive, CpuLoadReactive, Performance, Strobe, DualColorFlash. 12 theme presets. Per-zone control.",
            new[] { "rgb", "light", "lighting", "color", "colour", "led", "effect", "theme", "pulse", "rainbow", "wave", "strobe", "zone", "openrgb" },
            3, "Go to RGB Control tab"));

        _entries.Add(new AediEntry("OpenRGB", "RGB server integration",
            "FANZi auto-starts OpenRGB server on launch. Connects to port 6742. Auto-downloads if not installed. Watchdog auto-reconnects.",
            new[] { "openrgb", "rgb server", "sdk", "port 6742", "connect", "device", "auto start" },
            3, "RGB server status in RGB tab"));

        _entries.Add(new AediEntry("RGB Themes", "Preset lighting themes",
            "Ocean, Inferno, Glacier, Neon, Nature, Sunset, Spectrum, Blood Moon, Arctic, Temp Reactive, Performance, Strobe.",
            new[] { "theme", "preset", "ocean", "inferno", "glacier", "neon", "nature", "sunset", "spectrum", "blood moon", "arctic" },
            3, "Theme presets in RGB tab"));

        // Tasks
        _entries.Add(new AediEntry("Task Manager", "Process management",
            "View all processes with CPU%, memory, threads, handles. Multi-select and End Task. Filter by name. Kill process trees.",
            new[] { "task", "tasks", "process", "processes", "kill", "end task", "cpu", "memory", "ram", "multi select", "filter" },
            4, "Go to Tasks tab"));

        _entries.Add(new AediEntry("End Task", "Kill processes",
            "Select one or multiple processes and click End Task to kill them. Supports process tree killing. Select All / Select None buttons.",
            new[] { "end task", "kill", "terminate", "close", "stop", "process", "multi select", "select all" },
            4, "End Task in Tasks tab"));

        // Network
        _entries.Add(new AediEntry("Network Manager", "Bandwidth & port monitoring",
            "Per-adapter throughput, TOP downloaders/uploaders, process groups, port scanner, speed limits. NetLimiter-style bandwidth control.",
            new[] { "network", "net", "bandwidth", "download", "upload", "speed", "port", "scanner", "limit", "throttle", "netlimiter", "adapter" },
            5, "Go to Network tab"));

        _entries.Add(new AediEntry("Port Scanner", "Scan TCP ports",
            "Scan any host:port range. Shows open/closed/filtered status with process ownership. Close ports by killing owning process.",
            new[] { "port", "ports", "scan", "scanner", "tcp", "open", "closed", "filtered", "listening", "close port" },
            5, "Port Scanner in Network tab"));

        _entries.Add(new AediEntry("Speed Limit", "Bandwidth throttling",
            "Apply download/upload speed limits per process or process group. Uses Windows QoS policies. Requires admin.",
            new[] { "speed limit", "bandwidth", "throttle", "limit", "download limit", "upload limit", "qos", "kbps" },
            5, "Speed Limits in Network tab"));

        _entries.Add(new AediEntry("Process Groups", "Grouped bandwidth control",
            "Premade groups: Browsers, Gaming, Streaming, Downloads, Communication. Create custom groups. Apply limits to entire group at once.",
            new[] { "group", "groups", "browsers", "gaming", "streaming", "downloads", "communication", "custom group", "process group" },
            5, "Process Groups in Network tab"));

        // Power
        _entries.Add(new AediEntry("Power Monitor", "Energy & drive monitoring",
            "Per-component power draw (CPU, GPU, RAM, PSU). Drive info with size/free/format. Quick-launch Device Manager, Disk Management.",
            new[] { "power", "watt", "energy", "draw", "component", "drive", "disk", "storage", "device manager" },
            6, "Go to Power tab"));

        // System Cleaner
        _entries.Add(new AediEntry("System Cleaner", "Cache & temp file cleanup",
            "RAM trim, DNS flush, TEMP cleaner, cache flusher, browser caches, Windows Update cache, recycle bin, prefetch. 20+ clean targets.",
            new[] { "clean", "cleaner", "cache", "temp", "ram", "dns", "flush", "recycle", "prefetch", "browser", "junk", "free space" },
            7, "Go to System Cleaner tab"));

        _entries.Add(new AediEntry("RAM Cleaner", "Memory trim",
            "Trims all process working sets to free physical RAM. Uses EmptyWorkingSet API. Instant memory recovery.",
            new[] { "ram", "memory", "trim", "clean", "free", "working set", "physical memory" },
            7, "RAM Cleaner in System Cleaner"));

        _entries.Add(new AediEntry("DNS Cleaner", "Flush DNS cache",
            "Flushes the Windows DNS resolver cache. Fixes stale DNS entries and connectivity issues.",
            new[] { "dns", "flush", "cache", "resolver", "network", "connectivity" },
            7, "DNS Flush in System Cleaner"));

        _entries.Add(new AediEntry("Temp Cleaner", "Temporary file removal",
            "Cleans user temp, Windows temp, prefetch, browser caches (Chrome, Edge, Firefox, Brave), app caches (Teams, Discord, Spotify, NVIDIA).",
            new[] { "temp", "temporary", "clean", "prefetch", "browser cache", "chrome", "edge", "firefox", "teams", "discord" },
            7, "Temp Cleaner in System Cleaner"));

        _entries.Add(new AediEntry("Deep Cleaner", "Registry & startup manager",
            "Installed programs uninstaller, startup entry manager, registry orphan scanner, privacy wipe, disk analyzer, duplicate file finder.",
            new[] { "deep", "registry", "startup", "uninstall", "programs", "privacy", "disk analyzer", "duplicate", "orphan" },
            7, "Deep Cleaner in System Cleaner"));

        // Alerts
        _entries.Add(new AediEntry("Email Alerts", "Thermal notification emails",
            "SMTP email alerts when CPU temperature exceeds threshold. Configure SMTP host, port, credentials. Test email button. 10-minute cooldown.",
            new[] { "email", "alert", "notification", "smtp", "warning", "thermal", "threshold", "gmail", "outlook" },
            8, "Go to Alerts tab"));

        // Settings
        _entries.Add(new AediEntry("Settings", "Application configuration",
            "Start with Windows, minimize to tray, close to tray, polling interval, AI auto-fan, anomaly detection, overlay transparency, tab visibility.",
            new[] { "settings", "config", "startup", "tray", "polling", "overlay", "transparent", "tab", "visibility" },
            9, "Go to Settings tab"));

        _entries.Add(new AediEntry("Overlay", "Mini transparent overlay",
            "Always-on-top mini window showing CPU temp, GPU temp, fan speed, load. Draggable. Toggle transparency on/off in Settings.",
            new[] { "overlay", "mini", "small", "transparent", "opacity", "always on top", "floating", "widget" },
            9, "Overlay settings in Settings tab"));

        // About
        _entries.Add(new AediEntry("About FANZi IO-nity", "Ionity Global",
            "FANZi IO-nity by Ionity Global (Pty) Ltd. AI Thermal Intelligence powered by AEDi (Antwerp Ecosystems Designs Ionity). www.ionity.co.za",
            new[] { "about", "ionity", "aedi", "antwerp", "ecosystems", "designs", "website", "www", "pty", "ltd", "johan", "van antwerp" },
            9, "About — www.ionity.co.za"));

        // Help
        _entries.Add(new AediEntry("Getting Started", "Quick start guide",
            "1) Run as Administrator. 2) Dashboard shows health + temps. 3) Fans tab for speed control. 4) RGB tab for lighting. 5) Settings for preferences.",
            new[] { "help", "start", "getting started", "how to", "guide", "begin", "first", "admin", "administrator" },
            0, "Dashboard — Getting Started"));

        _entries.Add(new AediEntry("Administrator", "Why admin is needed",
            "LibreHardwareMonitor requires ring-0 kernel driver access to read CPU/GPU temperature sensors. Run FANZi as Administrator for full functionality.",
            new[] { "admin", "administrator", "elevated", "permission", "ring 0", "kernel", "driver", "access denied" },
            0, "Run as Administrator"));
    }
}

public sealed class AediEntry
{
    public string Title { get; }
    public string Section { get; }
    public string Description { get; }
    public string[] Keywords { get; }
    public int TabIndex { get; }
    public string Action { get; }

    public AediEntry(string title, string section, string description, string[] keywords, int tabIndex, string action)
    {
        Title = title;
        Section = section;
        Description = description;
        Keywords = keywords;
        TabIndex = tabIndex;
        Action = action;
    }
}

public sealed record AediSearchResult(AediEntry Entry, int Score)
{
    public string DisplayTitle => Entry.Title;
    public string DisplaySection => Entry.Section;
    public string DisplayDescription => Entry.Description;
    public string DisplayAction => Entry.Action;
    public int TargetTab => Entry.TabIndex;
}
