using Fanzi.FanControl.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public interface IHardwareMonitorService : IDisposable
{
    Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<SetFanControlResult> SetFanControlAsync(string channelId, double percent, CancellationToken cancellationToken = default);

    Task<SetFanControlResult> RestoreAutomaticControlAsync(string channelId, CancellationToken cancellationToken = default);
}