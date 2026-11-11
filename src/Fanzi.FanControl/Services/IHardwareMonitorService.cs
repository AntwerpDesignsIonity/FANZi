using Fanzi.FanControl.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public interface IHardwareMonitorService : IDisposable
{
    /// <summary>Why sensor access failed to start (e.g. driver blocked); null when it started fine.</summary>
    string? InitializationError { get; }

    Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<SetFanControlResult> SetFanControlAsync(string channelId, double percent, CancellationToken cancellationToken = default);

    Task<SetFanControlResult> RestoreAutomaticControlAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 4.4: "Use BIOS fan settings". When true, fans on motherboard headers (Super IO / embedded controller) are left to
    /// the BIOS Smart Fan curve: FANZi hands them back on the next poll, reports them read-only and refuses manual
    /// duty for them. GPU and USB fan-hub channels are not affected. Default implementation: not supported.
    /// </summary>
    bool BiosFanControl { get => false; set { } }

    /// <summary>
    /// 4.4 Self-Doctor: closes and reopens the sensor driver when readings stopped. Fan headers are handed back to
    /// automatic / BIOS control first. Returns true when the driver reopened. Default: not supported.
    /// </summary>
    Task<bool> TryRecoverAsync() => Task.FromResult(false);

    /// <summary>
    /// 4.5 pump protection (default on). Pumps are read-only and stay on their BIOS / firmware speed; FANZi hands a
    /// pump it finds under software control back and refuses duty writes for it. Default implementation: not supported.
    /// </summary>
    bool PumpProtectionEnabled { get => true; set { } }

    /// <summary>4.5: highest duty a pump may be set to once the user unlocked pump control (60–100).</summary>
    double PumpMaxPercent { get => 100; set { } }

    /// <summary>4.5: the user's per-channel role choices (channel id → role); replaces the previous set.</summary>
    void SetChannelRoles(System.Collections.Generic.IReadOnlyDictionary<string, CoolingRole> roles) { }
}