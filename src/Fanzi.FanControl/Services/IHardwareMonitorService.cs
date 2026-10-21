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
}