using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// 4.5 RGB coexistence: who drives SMBus lighting (RAM, onboard) — the vendor app or FANZi. See <see cref="RgbCoexistence"/>.
/// The decision is taken before OpenRGB is started (so its SMBus detectors are already off) and re-checked every 15 s.
/// </summary>
public partial class RgbControlViewModel
{
    public IReadOnlyList<string> SmbusPolicyChoices { get; } = new[]
    {
        "Automatic — vendor app when it runs, otherwise FANZi",
        "Always leave RAM / onboard lighting to the vendor app",
        "FANZi drives RAM / onboard lighting",
    };

    /// <summary>0 Automatic, 1 Vendor, 2 FANZi.</summary>
    [ObservableProperty] private int _smbusPolicyIndex;

    /// <summary>True while FANZi keeps its hands off SMBus lighting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictBannerBackground))]
    [NotifyPropertyChangedFor(nameof(ConflictBannerBorder))]
    [NotifyPropertyChangedFor(nameof(ConflictBannerIcon))]
    [NotifyPropertyChangedFor(nameof(ConflictTitleColor))]
    private bool _smbusHandsOff;

    /// <summary>One line under the picker: what FANZi is doing about RAM / onboard lighting right now.</summary>
    [ObservableProperty] private string _smbusPolicyStatus = "FANZi drives RAM and onboard lighting.";

    /// <summary>True when the banner is the calm "sharing" notice, not the red blocker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictBannerBackground))]
    [NotifyPropertyChangedFor(nameof(ConflictBannerBorder))]
    [NotifyPropertyChangedFor(nameof(ConflictBannerIcon))]
    [NotifyPropertyChangedFor(nameof(ConflictTitleColor))]
    private bool _conflictIsSharing;

    public string ConflictBannerBackground => ConflictIsSharing ? "#1F1A08" : "#2A0A10";
    public string ConflictBannerBorder => ConflictIsSharing ? "#F59E0B" : "#EF4444";
    public string ConflictBannerIcon => ConflictIsSharing ? "🤝" : "⛔";
    public string ConflictTitleColor => ConflictIsSharing ? "#FCD34D" : "#FCA5A5";

    private SmbusPolicy Policy => SmbusPolicyIndex switch { 1 => SmbusPolicy.Vendor, 2 => SmbusPolicy.Fanzi, _ => SmbusPolicy.Auto };

    private Action<string>? _persistSmbusPolicy;
    private bool _applyingCoexistence;
    private bool _coexistenceInitialised;
    private int _releaseStreak;
    private bool _coexistenceRestarting;

    /// <summary>Loads the persisted policy and the save callback (called by MainWindowViewModel).</summary>
    public void ApplyCoexistenceSettings(string? policy, Action<string> persist)
    {
        _applyingCoexistence = true;
        try { SmbusPolicyIndex = RgbCoexistence.ParsePolicy(policy) switch { SmbusPolicy.Vendor => 1, SmbusPolicy.Fanzi => 2, _ => 0 }; }
        finally { _applyingCoexistence = false; }
        _persistSmbusPolicy = persist;
    }

    partial void OnSmbusPolicyIndexChanged(int value)
    {
        if (_applyingCoexistence) return;
        _persistSmbusPolicy?.Invoke(Policy.ToString());
        _releaseStreak = 99; // the user asked — no settle delay
        RefreshHardwareAdvisories();
    }

    /// <summary>
    /// Takes the decision for the running products and pushes it to the OpenRGB launcher and the RGB service.
    /// Returns true when the hands-off state changed (the caller restarts OpenRGB so its detector list matches).
    /// Going hands-off is immediate; taking the bus back waits for two consecutive clear checks (~30 s) so a vendor
    /// service that is only restarting does not get a second writer on its bus.
    /// </summary>
    internal bool EvaluateCoexistence(IReadOnlyList<RgbConflictDetector.Conflict> conflicts, out SmbusDecision decision)
    {
        bool maxsun = _rgbService is MaxsunSyncRgbService { OwnsRam: true };
        decision = RgbCoexistence.Decide(Policy, conflicts.Select(c => c.Product), maxsun);

        bool target = decision.HandsOff;
        if (_coexistenceInitialised && _smbusHandsOffApplied && !target)
        {
            if (++_releaseStreak < 2) { target = true; }
        }
        if (target) { if (decision.HandsOff) _releaseStreak = 0; }

        bool changed = !_coexistenceInitialised || target != _smbusHandsOffApplied;
        _coexistenceInitialised = true;
        _smbusHandsOffApplied = target;

        string owner = decision.Owners.Count > 0 ? string.Join(", ", decision.Owners) : "the vendor app";
        _serverManager.SmbusHandsOff = target;
        _rgbService.SetSmbusHandsOff(target, owner);
        if (changed)
            AppLog.Info(target
                ? $"RGB coexistence: SMBus lighting left to {owner} (policy {Policy})."
                : $"RGB coexistence: FANZi drives SMBus lighting (policy {Policy}).");
        return changed;
    }

    private bool _smbusHandsOffApplied;

    /// <summary>Evaluates before the first OpenRGB start, off the UI thread.</summary>
    private void PrimeCoexistence()
    {
        if (!OperatingSystem.IsWindows() || !EndpointIsLocal) return;
        try
        {
            EvaluateCoexistence(RgbConflictDetector.Find(), out _);
        }
        catch (Exception ex) { AppLog.Info("RGB coexistence check failed: " + ex.Message); }
    }

    /// <summary>The hands-off state flipped while OpenRGB is running: restart it so its SMBus detectors match.</summary>
    private async Task ApplyCoexistenceChangeAsync()
    {
        if (_coexistenceRestarting || !EndpointIsLocal || !RgbEnabled) return;
        _coexistenceRestarting = true;
        try
        {
            if (_rgbService.IsConnected || IsServerRunning)
                await RestartServerForCompatibilityAsync(force: true);
        }
        catch (Exception ex) { AppLog.Info("RGB coexistence restart failed: " + ex.Message); }
        finally { _coexistenceRestarting = false; }
    }

    /// <summary>Banner text for the current conflicts + decision. Pure — unit-tested.</summary>
    internal static (string Title, string Message, bool Sharing) BuildCoexistenceBanner(
        IReadOnlyList<RgbConflictDetector.Conflict> conflicts, SmbusDecision decision)
    {
        if (conflicts.Count == 0) return (string.Empty, string.Empty, false);

        var ownerSet = new HashSet<string>(decision.Owners, StringComparer.OrdinalIgnoreCase);
        var others = conflicts.Where(c => !ownerSet.Contains(c.Product)).ToArray();

        if (decision.HandsOff && decision.Owners.Count > 0)
        {
            string msg = decision.Message;
            if (others.Length > 0)
                msg += $" {RgbConflictDetector.Describe(others)} also repaints USB / GPU lighting and will overwrite FANZi there.";
            return (decision.Title, msg, true);
        }
        if (decision.Risky)
            return (decision.Title, decision.Message, false);

        string title = $"{RgbConflictDetector.Describe(conflicts)} {(conflicts.Count == 1 ? "is" : "are")} controlling your lights";
        return (title, BuildConflictMessage(conflicts), false);
    }
}
