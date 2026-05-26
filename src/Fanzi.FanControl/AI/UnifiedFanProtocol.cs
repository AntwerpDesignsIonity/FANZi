using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.AI;

/// <summary>
/// Vendor-agnostic fan adapter interface — the "open standard" missing from the PC industry.
///
/// The PC fan ecosystem is fragmented: Corsair iCUE, Lian Li L-Connect, NZXT CAM,
/// Phanteks NEON, ASUS Armoury Crate, Cooler Master MasterPlus, etc. all use proprietary
/// hubs, daisy-chains, and bloated background services.
///
/// FANZI defines IFanAdapter as a unified contract. Every vendor backend (Corsair iCUE
/// REST API, Lian Li L-Connect HTTP API, OpenRGB SDK for fans, raw motherboard PWM via
/// LibreHardwareMonitor) implements this same shape. The UI doesn't care which backend
/// owns a fan — they all look identical.
///
/// Built-in adapters live in <see cref="FanAdapterRegistry"/>. Plugin authors can ship
/// their own adapter assemblies and drop them into a plugins folder.
/// </summary>
public interface IFanAdapter : IDisposable
{
    /// <summary>Human-readable backend name (e.g. "Motherboard PWM", "OpenRGB Fans", "Corsair iCUE").</summary>
    string BackendName { get; }

    /// <summary>True if this backend is currently reachable.</summary>
    bool IsAvailable { get; }

    /// <summary>Enumerate every fan/pump channel this backend can see.</summary>
    Task<IReadOnlyList<UnifiedFanChannel>> EnumerateAsync(CancellationToken ct = default);

    /// <summary>Send a duty-cycle (0-100%) to the given channel.</summary>
    Task SetDutyAsync(string channelId, double percent, CancellationToken ct = default);

    /// <summary>Read current RPM + duty from the channel.</summary>
    Task<UnifiedFanReading> ReadAsync(string channelId, CancellationToken ct = default);
}

/// <summary>One fan/pump channel exposed by some backend.</summary>
public sealed record UnifiedFanChannel(
    string Id,
    string DisplayName,
    string Backend,
    FanChannelKind Kind,
    bool SupportsWrite,
    int? MaxRpm);

public enum FanChannelKind { CaseFan, CpuFan, GpuFan, Pump, AioFan, Unknown }

public sealed record UnifiedFanReading(double? Rpm, double? DutyPercent, double? TempC);

/// <summary>
/// Registry of all loaded fan-adapter backends. The Engine fuses their channel lists into
/// one unified device tree the UI binds to.
/// </summary>
public sealed class FanAdapterRegistry
{
    private readonly List<IFanAdapter> _adapters = new();

    public IReadOnlyList<IFanAdapter> Adapters => _adapters;

    public void Register(IFanAdapter adapter) => _adapters.Add(adapter);

    /// <summary>Returns every available fan/pump from every registered backend, deduplicated by ID.</summary>
    public async Task<IReadOnlyList<UnifiedFanChannel>> EnumerateAllAsync(CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UnifiedFanChannel>();

        foreach (var adapter in _adapters)
        {
            if (!adapter.IsAvailable) continue;

            try
            {
                var channels = await adapter.EnumerateAsync(ct);
                foreach (var ch in channels)
                {
                    var key = $"{ch.Backend}::{ch.Id}";
                    if (seen.Add(key))
                        result.Add(ch);
                }
            }
            catch
            {
                // Adapter died — skip, keep enumerating others
            }
        }

        return result;
    }

    public void DisposeAll()
    {
        foreach (var a in _adapters)
        {
            try { a.Dispose(); } catch { }
        }
        _adapters.Clear();
    }
}
