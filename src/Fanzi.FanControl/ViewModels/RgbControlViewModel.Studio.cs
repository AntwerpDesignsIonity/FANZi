using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.ViewModels;

/// <summary>An effect button in the RGB studio, painted in the effect's own colours.</summary>
public sealed record RgbEffectChip(RgbEffectType Effect, string Label, string Glyph, IBrush Background, string Hint);

/// <summary>A one-click mood prompt chip.</summary>
public sealed record RgbMoodChip(string Prompt, IBrush Background);

/// <summary>
/// v4 RGB Studio: colourful effect chips, quick swatches, colour harmony, AEDi mood-to-lighting,
/// Smart Ambient / Thermal Guardian / Night dimming (see <see cref="RgbAiDirector"/>), device identify and
/// the deep hardware scan (<see cref="RgbHardwareInventory"/>).
/// </summary>
public sealed partial class RgbControlViewModel
{
    // ── Effect chips (each painted in its own colours — "RGB buttons") ─────────

    public IReadOnlyList<RgbEffectChip> EffectChips { get; } =
    [
        new(RgbEffectType.Static, "Static", "■", Gradient("#2563EB", "#60A5FA"), "One solid colour"),
        new(RgbEffectType.Pulse, "Breathe", "◐", Gradient("#7C3AED", "#22D3EE"), "Fades in and out"),
        new(RgbEffectType.Rainbow, "Rainbow", "◎", Rainbow(), "Full-spectrum hue cycle"),
        new(RgbEffectType.ColorWave, "Wave", "≈", Gradient("#06B6D4", "#A855F7"), "Primary → secondary wave across devices"),
        new(RgbEffectType.TemperatureReactive, "Thermal", "♨", Gradient("#38BDF8", "#EF4444"), "Colour follows CPU/GPU temperature"),
        new(RgbEffectType.CpuLoadReactive, "CPU load", "▲", Gradient("#22C55E", "#F59E0B"), "Pace and brightness follow CPU load"),
        new(RgbEffectType.Performance, "Performance", "⚡", Gradient("#2563EB", "#F43F5E"), "Load drives speed, temperature drives colour"),
        new(RgbEffectType.Strobe, "Strobe", "✦", Gradient("#F8FAFC", "#64748B"), "Rapid flash"),
        new(RgbEffectType.DualColorFlash, "Dual flash", "◑", Gradient("#EC4899", "#06B6D4"), "Alternates primary and secondary"),
    ];

    /// <summary>One-click primary colours.</summary>
    public IReadOnlyList<RgbColor> QuickSwatches { get; } =
    [
        RgbColor.Red, new(255, 60, 0), RgbColor.Orange, RgbColor.Yellow, new(150, 255, 0), RgbColor.Green,
        RgbColor.Teal, RgbColor.Cyan, RgbColor.IceBlue, RgbColor.Blue, new(37, 99, 235), RgbColor.Purple,
        new(190, 0, 255), RgbColor.Magenta, new(255, 80, 170), RgbColor.White,
    ];

    public IReadOnlyList<RgbMoodChip> MoodChips { get; } =
    [
        new("calm ocean", Gradient("#0064FF", "#00DCFF")),
        new("fast cyberpunk party", Gradient("#FF00C8", "#00E6FF")),
        new("lava fire", Gradient("#FF2800", "#FFA000")),
        new("purple galaxy", Gradient("#5A00FF", "#FF00AA")),
        new("focus work", Gradient("#FFF4E5", "#96C8FF")),
        new("gaming", Gradient("#FF0028", "#0078FF")),
        new("south africa", Gradient("#007A4D", "#FFB81C")),
        new("dim night", Gradient("#141E8C", "#5A28C8")),
    ];

    public IReadOnlyList<string> HarmonyRules { get; } = ["Complementary", "Analogous", "Triadic", "Split", "Monochrome"];

    public IRelayCommand<RgbEffectType> SelectEffectCommand { get; private set; } = null!;
    public IRelayCommand<RgbColor> SetPrimarySwatchCommand { get; private set; } = null!;
    public IRelayCommand<RgbColor> SetSecondarySwatchCommand { get; private set; } = null!;
    public IRelayCommand SwapColorsCommand { get; private set; } = null!;
    public IRelayCommand<string> ApplyHarmonyCommand { get; private set; } = null!;
    public IRelayCommand GenerateMoodCommand { get; private set; } = null!;
    public IRelayCommand<string> ApplyMoodChipCommand { get; private set; } = null!;
    public IRelayCommand AiSuggestCommand { get; private set; } = null!;
    public IAsyncRelayCommand DeepScanCommand { get; private set; } = null!;
    public IAsyncRelayCommand<RgbDeviceInfo> IdentifyDeviceCommand { get; private set; } = null!;

    // ── AEDi lighting ─────────────────────────────────────────────────────────

    [ObservableProperty] private string _moodPrompt = "";
    [ObservableProperty] private string _aiLightingStatus = "AEDi lighting: pick an effect, describe a mood or let Smart Ambient drive it.";
    [ObservableProperty] private bool _smartAmbientEnabled;
    [ObservableProperty] private bool _thermalGuardianEnabled = true;
    [ObservableProperty] private bool _nightDimmingEnabled;
    [ObservableProperty] private string _aiModeLabel = "Manual";
    [ObservableProperty] private string _aiModeColor = "#6A8AAF";

    private readonly RgbAiDirector _aiDirector = new();
    private volatile RgbAiDecision? _aiOverride;
    private bool _isGameMode;
    private string? _gameName;
    private Action<bool, bool, bool>? _persistAi;
    private bool _applyingAiSettings;

    // ── Deep scan ─────────────────────────────────────────────────────────────

    public ObservableCollection<DeepScanFinding> DeepScanFindings { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DeepScanButtonText))] private bool _isDeepScanning;

    public string DeepScanButtonText => IsDeepScanning ? "Scanning…" : "🔍 Run deep scan";
    [ObservableProperty] private string _deepScanSummary = "Deep scan cross-checks OpenRGB against Windows: RAM sticks, fan headers, motherboard, GPU and USB RGB controllers.";
    [ObservableProperty] private bool _hasDeepScanResults;

    /// <summary>Number of fan headers LibreHardwareMonitor reads (set by MainWindowViewModel).</summary>
    public Func<int>? FanHeaderCountProvider { get; set; }

    /// <summary>Device index currently flashing for "Identify" (-1 none); the frame loop skips it.</summary>
    private volatile int _identifyingDevice = -1;

    private void InitializeStudio()
    {
        SelectEffectCommand = new RelayCommand<RgbEffectType>(effect =>
        {
            SelectedEffect = effect;
            ActiveTheme = null;
            AiLightingStatus = $"Effect: {EffectChips.First(c => c.Effect == effect).Label} — {EffectChips.First(c => c.Effect == effect).Hint}.";
        });
        SetPrimarySwatchCommand = new RelayCommand<RgbColor>(c => { PrimaryColor = c; SyncPrimarySliders(); });
        SetSecondarySwatchCommand = new RelayCommand<RgbColor>(c => { SecondaryColor = c; SyncSecondarySliders(); });
        SwapColorsCommand = new RelayCommand(() =>
        {
            (PrimaryColor, SecondaryColor) = (SecondaryColor, PrimaryColor);
            SyncPrimarySliders();
            SyncSecondarySliders();
        });
        ApplyHarmonyCommand = new RelayCommand<string>(ApplyHarmony);
        GenerateMoodCommand = new RelayCommand(() => ApplyMood(MoodPrompt));
        ApplyMoodChipCommand = new RelayCommand<string>(p => { MoodPrompt = p ?? ""; ApplyMood(MoodPrompt); });
        AiSuggestCommand = new RelayCommand(SuggestTheme);
        DeepScanCommand = new AsyncRelayCommand(DeepScanAsync);
        IdentifyDeviceCommand = new AsyncRelayCommand<RgbDeviceInfo>(IdentifyDeviceAsync);
    }

    private void ApplyHarmony(string? rule)
    {
        var r = rule switch
        {
            "Analogous" => RgbColorHarmony.Rule.Analogous,
            "Triadic" => RgbColorHarmony.Rule.Triadic,
            "Split" => RgbColorHarmony.Rule.SplitComplementary,
            "Monochrome" => RgbColorHarmony.Rule.Monochrome,
            _ => RgbColorHarmony.Rule.Complementary,
        };
        SecondaryColor = RgbColorHarmony.Partner(PrimaryColor, r);
        SyncSecondarySliders();
        AiLightingStatus = $"Colour harmony: {RgbColorHarmony.Label(r)} partner for {PrimaryColor.ToHex()} is {SecondaryColor.ToHex()}.";
    }

    private void ApplyMood(string? prompt)
    {
        var mood = RgbMoodEngine.Generate(prompt);
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            ActiveTheme = null;
            SelectedEffect = mood.Effect;
            PrimaryColor = mood.Primary;
            SecondaryColor = mood.Secondary;
            SpeedMultiplier = mood.Speed;
            Brightness = mood.Brightness;
            SyncPrimarySliders();
            SyncSecondarySliders();
        }
        AiLightingStatus = "AEDi mood: " + mood.Explanation;
    }

    private void SuggestTheme()
    {
        var (preset, reason) = RgbAiDirector.Suggest(CurrentAiInputs());
        ApplyTheme(preset);
        AiLightingStatus = $"AEDi suggests {preset.Emoji} {preset.Name}: {reason}";
    }

    private RgbAiInputs CurrentAiInputs()
        => new(_cpuTempC, _gpuTempC, _cpuLoadPct, _isGameMode, _gameName, DateTime.Now.TimeOfDay);

    /// <summary>Game state from the dashboard's detector (drives Smart Ambient's gaming scene).</summary>
    public void UpdateGameState(bool isGameMode, string? gameName)
    {
        _isGameMode = isGameMode;
        _gameName = gameName;
    }

    /// <summary>Loads the persisted AI switches and the save callback (called by MainWindowViewModel).</summary>
    public void ApplyAiSettings(bool smartAmbient, bool thermalGuardian, bool nightDimming, Action<bool, bool, bool> persist)
    {
        _applyingAiSettings = true;
        try
        {
            SmartAmbientEnabled = smartAmbient;
            ThermalGuardianEnabled = thermalGuardian;
            NightDimmingEnabled = nightDimming;
        }
        finally { _applyingAiSettings = false; }
        _persistAi = persist;
        ReevaluateAi();
    }

    partial void OnSmartAmbientEnabledChanged(bool value) => OnAiSwitchChanged();
    partial void OnThermalGuardianEnabledChanged(bool value) => OnAiSwitchChanged();
    partial void OnNightDimmingEnabledChanged(bool value) => OnAiSwitchChanged();

    private void OnAiSwitchChanged()
    {
        ReevaluateAi();
        if (!_applyingAiSettings) _persistAi?.Invoke(SmartAmbientEnabled, ThermalGuardianEnabled, NightDimmingEnabled);
    }

    /// <summary>Runs the director on the latest readings; called on every hardware update and switch change.</summary>
    private void ReevaluateAi()
    {
        _aiDirector.SmartAmbient = SmartAmbientEnabled;
        _aiDirector.ThermalGuardian = ThermalGuardianEnabled;
        _aiDirector.NightDimming = NightDimmingEnabled;

        var previous = _aiOverride;
        var decision = _aiDirector.Decide(CurrentAiInputs(), PrimaryColor, SecondaryColor);
        _aiOverride = decision;

        void Publish()
        {
            if (decision is null)
            {
                AiModeLabel = "Manual";
                AiModeColor = "#6A8AAF";
                if (previous is not null)
                    AiLightingStatus = "AEDi lighting released — your own effect is back.";
                return;
            }

            AiModeLabel = decision.Mode switch
            {
                RgbAiMode.Critical => "THERMAL ALERT",
                RgbAiMode.Hot => "Hot",
                RgbAiMode.Gaming => "Gaming",
                RgbAiMode.Working => "Working",
                RgbAiMode.Night => "Night",
                _ => "Idle",
            };
            AiModeColor = decision.Mode switch
            {
                RgbAiMode.Critical => "#EF4444",
                RgbAiMode.Hot => "#FB923C",
                RgbAiMode.Gaming => "#F43F5E",
                RgbAiMode.Working => "#FBBF24",
                RgbAiMode.Night => "#818CF8",
                _ => "#34D399",
            };
            if (previous is null || previous.Mode != decision.Mode)
                AiLightingStatus = decision.Reason;
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Publish();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Publish);
    }

    /// <summary>Effect parameters for this frame: the AI override when active, else the user's settings.</summary>
    private (RgbEffectType Effect, RgbColor Primary, RgbColor Secondary, double Speed, double Brightness) FrameParameters()
    {
        var ai = _aiOverride;
        if (ai is null) return (SelectedEffect, PrimaryColor, SecondaryColor, SpeedMultiplier, Brightness);
        double brightness = ai.Mode == RgbAiMode.Critical ? 1.0 : Math.Min(Brightness, ai.BrightnessCap);
        return (ai.Effect, ai.Primary, ai.Secondary, ai.Speed, brightness);
    }

    // ── Identify a device (flash it white) ────────────────────────────────────

    private async Task IdentifyDeviceAsync(RgbDeviceInfo? device)
    {
        if (device is null || !_rgbService.IsConnected) return;
        _identifyingDevice = device.DeviceIndex;
        AiLightingStatus = $"Identifying {device.DisplayName} — watch for the white flashes.";
        try
        {
            for (int i = 0; i < 3; i++)
            {
                await _rgbService.SetDeviceColorAsync(device.DeviceIndex, RgbColor.White, _cts.Token);
                await Task.Delay(260, _cts.Token);
                await _rgbService.SetDeviceColorAsync(device.DeviceIndex, RgbColor.Black, _cts.Token);
                await Task.Delay(200, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn($"Identify '{device.Name}' failed", ex); }
        finally { _identifyingDevice = -1; }
    }

    // ── Deep scan ─────────────────────────────────────────────────────────────

    private async Task DeepScanAsync()
    {
        if (IsDeepScanning) return;
        IsDeepScanning = true;
        DeepScanSummary = "Deep scan running — querying OpenRGB, WMI (RAM, board, GPU), USB RGB vendors and the PawnIO driver…";
        try
        {
            if (_rgbService.IsConnected) await RefreshDevicesAsync();
            var system = OperatingSystem.IsWindows()
                ? await RgbHardwareInventory.ReadSystemAsync(_cts.Token)
                : new SystemRgbInventory("Unknown", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<(string, string, string)>(), false);
            int fans = FanHeaderCountProvider?.Invoke() ?? 0;
            var findings = RgbHardwareInventory.Analyse(Devices.ToList(), system, fans, _rgbService.IsConnected);

            DeepScanFindings.Clear();
            foreach (var f in findings) DeepScanFindings.Add(f);
            HasDeepScanResults = DeepScanFindings.Count > 0;

            int ok = findings.Count(f => f.Status == DeepScanStatus.Ok);
            int missing = findings.Count(f => f.Status == DeepScanStatus.Missing);
            DeepScanSummary = $"Deep scan done at {DateTime.Now:HH:mm:ss}: {Devices.Count} OpenRGB device(s), {system.MemoryModules.Count} RAM stick(s), " +
                              $"{fans} fan header(s), {system.RgbUsbCandidates.Count} USB RGB-vendor device(s) — {ok} OK, {missing} missing.";
            AppLog.Info("RGB deep scan: " + DeepScanSummary);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("RGB deep scan failed", ex);
            DeepScanSummary = $"Deep scan failed: {ex.Message}";
        }
        finally
        {
            IsDeepScanning = false;
        }
    }

    // ── Device list (in-place, numbered duplicates) ───────────────────────────

    /// <summary>Numbers identical names ("ENE DRAM · stick 1/2") and syncs the list without a reset.</summary>
    private void SyncDevices(IReadOnlyList<RgbDeviceInfo> list)
    {
        var counts = list.GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        var numbered = list.Select(d =>
        {
            if (counts[d.Name] < 2) return d;
            seen[d.Name] = seen.TryGetValue(d.Name, out int n) ? n + 1 : 1;
            return d with { Slot = seen[d.Name] };
        }).ToList();

        CollectionSync.Sync(Devices, numbered, d => (d.DeviceIndex, d.Name));
    }

    // ── Brushes ───────────────────────────────────────────────────────────────

    internal static IBrush Gradient(string a, string b) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(a), 0), new GradientStop(Color.Parse(b), 1) },
    }.ToImmutable();

    internal static IBrush Rainbow() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#FF0040"), 0), new GradientStop(Color.Parse("#FF9900"), 0.2),
            new GradientStop(Color.Parse("#E6FF00"), 0.38), new GradientStop(Color.Parse("#00FF88"), 0.55),
            new GradientStop(Color.Parse("#00B3FF"), 0.72), new GradientStop(Color.Parse("#8A2BFF"), 0.88),
            new GradientStop(Color.Parse("#FF00C8"), 1),
        },
    }.ToImmutable();
}
