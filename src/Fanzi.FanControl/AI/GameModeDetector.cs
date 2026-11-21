using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.AI;

/// <summary>
/// AI Game Mode Detector — automatically detects when games or heavy GPU applications
/// are running and suggests/applies optimal fan and RGB profiles.
/// Uses process name matching, GPU load correlation, and fullscreen detection.
/// </summary>
public sealed class GameModeDetector
{
    /// <summary>Processes that are a game: running one is Game Mode, whatever the load.</summary>
    private static readonly HashSet<string> KnownGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // AAA Titles
        "Cyberpunk2077", "witcher3", "witcher4", "reddeadredemption2", "rdr2",
        "GTA5", "gta-online", "EldenRing", "armoredcore6",
        "Starfield", "BaldursGate3", "BG3", "DiabloIV", "DiabloV",
        "HogwartsLegacy", "AtomicHeart", "Remnant2", "LiesofP",
        "AlanWake2", "RoboCop", "TheCrewMotofest",
        // Popular Games
        "FortniteClient-Win64-Shipping",
        "VALORANT-Win64-Shipping", "Valorant",
        "Overwatch", "cs2", "csgo", "dota2", "TeamFortress2",
        "League of Legends",
        "ApexLegends", "r5apex", "destiny2",
        "Warframe", "Warframe.x64", "DeepRockGalactic",
        "Minecraft", "MinecraftJava",
        "GenshinImpact", "YuanShen", "HonkaiStarRail",
        "PUBG-Win64-Shipping", "TslGame", "DayZSA_x64",
        "RustClient", "Arma3_x64", "ArmaReforger",
        "RocketLeague", "RainbowSix", "RainbowSix_Vulkan",
        "DeadByDaylight-Win64-Shipping", "Among Us",
        "Palworld-Win64-Shipping", "LethalCompany",
        // Emulators
        "RPCS3", "dolphin", "Cemu", "yuzu", "Ryujinx", "xenia",
    };

    /// <summary>
    /// 4.6: processes that only count together with real GPU work — engines / editors, creative apps, capture tools
    /// and Java (Minecraft). Before 4.6 these — and the store launchers (Steam, Battle.net, Epic, EA, Ubisoft, GOG,
    /// Riot), which sit in the tray all day — were listed as games, so a PC with Steam open was in "Game Mode"
    /// permanently. Launchers are no longer listed at all.
    /// </summary>
    private static readonly HashSet<string> HeavyAppProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "javaw", "UnrealEditor", "Unity", "Godot",
        "davinciresolve", "Resolve", "AfterFX", "Premiere", "Adobe Premiere Pro",
        "Blender", "Cinema 4D", "maya", "3dsmax",
        "obs64", "obs32", "Streamlabs", "XSplit",
    };

    /// <summary>Average GPU load (%) a <see cref="HeavyAppProcesses"/> entry needs before it counts.</summary>
    internal const double HeavyAppGpuLoadPercent = 40;

    /// <summary>Game Mode ends once the game's process is gone and the GPU average is below this (%).</summary>
    internal const double ExitGpuLoadPercent = 40;

    /// <summary>True when the process name is a game on its own (no load needed).</summary>
    internal static bool IsKnownGame(string processName) => KnownGameProcesses.Contains(processName);

    /// <summary>True when the process only counts with GPU load behind it.</summary>
    internal static bool IsHeavyApp(string processName) => HeavyAppProcesses.Contains(processName);

    private readonly Queue<GameSample> _samples = new();
    private const int MaxSamples = 30;
    private bool _isGameModeActive;
    private string _detectedGame = "";
    // Written on the UI thread (Update), read by the process scan on a pool thread.
    private double _averageGpuLoad;
    private DateTime _gameModeActivated = DateTime.MinValue;
    private readonly Func<(bool IsGameRunning, string GameName)>? _processScanOverride;

    public GameModeDetector()
    {
    }

    /// <param name="processScan">Replaces the live process-list scan (tests/render harness), so
    /// detection does not depend on what happens to run on the machine.</param>
    internal GameModeDetector(Func<(bool IsGameRunning, string GameName)> processScan)
    {
        _processScanOverride = processScan;
    }

    public bool IsGameModeActive => _isGameModeActive;
    public string DetectedGame => _detectedGame;
    public TimeSpan GameModeDuration => _isGameModeActive
        ? DateTime.UtcNow - _gameModeActivated
        : TimeSpan.Zero;
    public string StatusMessage => _isGameModeActive
        ? $"🎮 Game Mode: {_detectedGame} ({GameModeDuration.TotalMinutes:F0}m)"
        : "Desktop mode";
    /// <summary>Game name and session length for the dashboard tile's detail line
    /// (the tile title stays a short static "🎮 Game Mode" so it never trims).</summary>
    public string DetailMessage => _isGameModeActive
        ? $"{(string.IsNullOrEmpty(_detectedGame) ? "Sustained GPU load" : _detectedGame)} · {GameModeDuration.TotalMinutes:F0} min"
        : "";

    public async Task<(bool IsGameRunning, string GameName)> DetectAsync(CancellationToken ct = default)
    {
        if (_processScanOverride is not null)
            return _processScanOverride();

        return await Task.Run(() =>
        {
            Process[] processes = Array.Empty<Process>();
            try
            {
                processes = Process.GetProcesses();
                string heavyApp = "";
                foreach (var p in processes)
                {
                    try
                    {
                        // ProcessName only — HasExited needs a process handle and throws for protected processes.
                        string name = p.ProcessName;

                        if (KnownGameProcesses.Contains(name))
                        {
                            return (true, name);
                        }

                        // Unreal "…-Win64-Shipping" builds are games whatever they are called.
                        if (IsLikelyGameProcess(p))
                            return (true, name);

                        if (heavyApp.Length == 0 && HeavyAppProcesses.Contains(name))
                            heavyApp = name;
                    }
                    catch { }
                }

                // Editors / creative apps / Java count only while the GPU is actually working.
                if (heavyApp.Length > 0 && Volatile.Read(ref _averageGpuLoad) >= HeavyAppGpuLoadPercent)
                    return (true, heavyApp);
            }
            catch { }
            finally
            {
                // v4.0.1: dispose every Process (an early "game found" return used to leak the rest's handles).
                foreach (var p in processes) p.Dispose();
            }

            return (false, "");
        }, ct);
    }

    public void Update(double gpuLoadPercent, double cpuTempC)
    {
        var sample = new GameSample(DateTime.UtcNow, gpuLoadPercent, cpuTempC);
        _samples.Enqueue(sample);
        while (_samples.Count > MaxSamples) _samples.Dequeue();

        // Game mode detection logic:
        // Sustained GPU load > 60% + high CPU temp = likely gaming
        if (_samples.Count >= 5)
        {
            double avgGpu = _samples.Average(s => s.GpuLoad);
            double avgCpuTemp = _samples.Average(s => s.CpuTemp);
            Volatile.Write(ref _averageGpuLoad, avgGpu);

            if (!_isGameModeActive && avgGpu > 60 && avgCpuTemp > 70)
            {
                _isGameModeActive = true;
                _gameModeActivated = DateTime.UtcNow;
            }
            // 4.6: load-triggered Game Mode (no process name) ends when the GPU goes quiet. The old rule also wanted
            // the CPU under 50 °C, which a warm-idling CPU never reaches — Game Mode then never ended.
            else if (_isGameModeActive && _detectedGame.Length == 0 && avgGpu < 20)
            {
                _isGameModeActive = false;
            }
        }
        else
        {
            Volatile.Write(ref _averageGpuLoad, _samples.Average(s => s.GpuLoad));
        }
    }

    public void SetDetectedGame(string gameName)
    {
        if (string.IsNullOrEmpty(gameName)) return;

        // 4.6: the scan repeats every few seconds — only a new session starts the clock (it used to restart on
        // every scan, so the tile read "0 min" forever and the "long session" profile was never suggested).
        if (!_isGameModeActive)
            _gameModeActivated = DateTime.UtcNow;
        _detectedGame = gameName;
        _isGameModeActive = true;
    }

    /// <summary>
    /// 4.6: the process scan found no game. A session that was started by a game process ends once the GPU has
    /// calmed down too (a game that is only alt-tabbed or loading keeps its session).
    /// </summary>
    public void SetNoGameRunning()
    {
        if (!_isGameModeActive || _detectedGame.Length == 0) return;
        if (_samples.Count > 0 && _samples.Average(s => s.GpuLoad) >= ExitGpuLoadPercent)
        {
            // Still under load without a known process: keep cooling for it, but stop naming a game that is gone.
            _detectedGame = "";
            return;
        }
        _isGameModeActive = false;
        _detectedGame = "";
    }

    public GameProfileSuggestion GetSuggestion()
    {
        if (!_isGameModeActive)
            return new GameProfileSuggestion("Balanced", "Standard desktop use", "Balanced");

        double duration = GameModeDuration.TotalMinutes;

        if (duration > 60)
            return new GameProfileSuggestion("Performance", "Extended gaming session — max cooling", "Gaming");

        if (_detectedGame.Contains("Fortnite", StringComparison.OrdinalIgnoreCase) ||
            _detectedGame.Contains("VALORANT", StringComparison.OrdinalIgnoreCase) ||
            _detectedGame.Contains("ApexLegends", StringComparison.OrdinalIgnoreCase) ||
            _detectedGame.Contains("cs2", StringComparison.OrdinalIgnoreCase) ||
            _detectedGame.Contains("Overwatch", StringComparison.OrdinalIgnoreCase))
            return new GameProfileSuggestion("Performance", "Competitive FPS — low latency cooling", "Gaming");

        if (_detectedGame.Contains("Cyberpunk", StringComparison.OrdinalIgnoreCase) ||
            _detectedGame.Contains("EldenRing", StringComparison.OrdinalIgnoreCase) ||
            _detectedGame.Contains("Starfield", StringComparison.OrdinalIgnoreCase) ||
            _detectedGame.Contains("Baldur", StringComparison.OrdinalIgnoreCase))
            return new GameProfileSuggestion("Performance", "AAA title — heavy GPU/CPU load", "Gaming");

        return new GameProfileSuggestion("Gaming", "Game detected — optimized cooling profile", "Gaming");
    }

    private static bool IsLikelyGameProcess(Process p)
    {
        try
        {
            string name = p.ProcessName.ToLowerInvariant();
            // Check for common game engine patterns
            if (name.Contains("shipping") && (name.Contains("win64") || name.Contains("win32")))
                return true;
            if (name.Contains("-win64-shipping") || name.Contains("-win32-shipping"))
                return true;
        }
        catch { }
        return false;
    }

    private readonly record struct GameSample(DateTime Timestamp, double GpuLoad, double CpuTemp);
}

public sealed record GameProfileSuggestion(string ProfileName, string Reason, string SuggestedRgbPreset);
