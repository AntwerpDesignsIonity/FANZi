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
}