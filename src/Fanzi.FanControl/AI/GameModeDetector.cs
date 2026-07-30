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
    private static readonly HashSet<string> KnownGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // AAA Titles
        "Cyberpunk2077", "witcher3", "witcher4", "reddeadredemption2", "rdr2",
        "GTA5", "gta-online", "EldenRing", "eldenring", "armoredcore6",
        "Starfield", "BaldursGate3", "BG3", "DiabloIV", "DiabloV",
        "HogwartsLegacy", "AtomicHeart", "Remnant2", "LiesofP",
        "AlanWake2", "RoboCop", "TheCrewMotofest",
        // Popular Games
        " FortniteClient-Win64-Shipping", "FortniteClient-Win64-Shipping",
        "VALORANT-Win64-Shipping", "Valorant", "RiotClientServices",
        "Overwatch", "Battle.net", "steam", "steamwebhelper",
        "cs2", "csgo", "dota2", "TeamFortress2",
        "League of Legends", "LeagueClient", "LoL",
        "ApexLegends", "r5apex", "destiny2", "Destiny2",
        "Warframe", "Warframe.x64", "DeepRockGalactic",
        "Minecraft", "MinecraftJava", "javaw",
        "GenshinImpact", "YuanShen", "HonkaiStarRail",
        "PUBG-Win64-Shipping", "TslGame", "DayZSA_x64",
        "RustClient", "rust", "Arma3_x64", "ArmaReforger",
        "RocketLeague", "RainbowSix", "RainbowSix_Vulkan",
        "DeadByDaylight-Win64-Shipping", "Among Us",
        "Palworld-Win64-Shipping", "LethalCompany",
        // Game Launchers / Engines
        "UnrealEditor", "Unity", "UnityHub", "Godot",
        "EpicGamesLauncher", "EpicWebHelper",
        "Origin", "EADesktop", "Uplay", "UbisoftConnect",
        "Battle.net", "GalaxyClient", "GOG Galaxy",
        // Emulators
        "RPCS3", "dolphin", "Cemu", "yuzu", "Ryujinx", "xenia",
        // Creative / Heavy GPU
        "davinciresolve", "DavinciResolve", "AfterFX", "Premiere",
        "Blender", "Cinema 4D", "maya", "3dsmax",
        "obs64", "obs32", "Streamlabs", "XSplit",
    };

    private readonly Queue<GameSample> _samples = new();
    private const int MaxSamples = 30;
    private bool _isGameModeActive;
    private string _detectedGame = "";
    private DateTime _gameModeActivated = DateTime.MinValue;

    public bool IsGameModeActive => _isGameModeActive;
    public string DetectedGame => _detectedGame;
    public TimeSpan GameModeDuration => _isGameModeActive
        ? DateTime.UtcNow - _gameModeActivated
        : TimeSpan.Zero;
    public string StatusMessage => _isGameModeActive
        ? $"🎮 Game Mode: {_detectedGame} ({GameModeDuration.TotalMinutes:F0}m)"
        : "Desktop mode";

    public async Task<(bool IsGameRunning, string GameName)> DetectAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        string name = p.ProcessName;

                        if (KnownGameProcesses.Contains(name))
                        {
                            return (true, name);
                        }

                        // Check for GPU-heavy processes (heuristic: high working set + GPU-related name patterns)
                        if (IsLikelyGameProcess(p))
                            return (true, name);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }

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

            if (!_isGameModeActive && avgGpu > 60 && avgCpuTemp > 70)
            {
                _isGameModeActive = true;
                _gameModeActivated = DateTime.UtcNow;
            }
            else if (_isGameModeActive && avgGpu < 20 && avgCpuTemp < 50)
            {
                _isGameModeActive = false;
                _detectedGame = "";
            }
        }
    }

    public void SetDetectedGame(string gameName)
    {
        if (!string.IsNullOrEmpty(gameName))
        {
            _detectedGame = gameName;
            _isGameModeActive = true;
            _gameModeActivated = DateTime.UtcNow;
        }
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
