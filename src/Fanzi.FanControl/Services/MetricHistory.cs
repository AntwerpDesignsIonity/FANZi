using System;
using System.Collections.Generic;

namespace Fanzi.FanControl.Services;

/// <summary>One timestamped reading in a <see cref="MetricHistory"/>.</summary>
public readonly record struct HistorySample(DateTime TimeUtc, double Value);

/// <summary>
/// Rolling history of one metric: at most <see cref="Capacity"/> samples, none older than
/// <see cref="MaxAge"/> relative to the newest. Pure (no clock, no UI); the caller supplies timestamps.
/// </summary>
public sealed class MetricHistory
{
    /// <summary>15 minutes at a 1 s poll — the longest window the dashboard offers.</summary>
    public const int DefaultCapacity = 900;

    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(15);

    private readonly RingBuffer<HistorySample> _samples;

    public MetricHistory(int capacity = DefaultCapacity, TimeSpan? maxAge = null)
    {
        _samples = new RingBuffer<HistorySample>(capacity);
        MaxAge = maxAge ?? DefaultMaxAge;
        if (MaxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge), MaxAge, "Max age must be positive.");
    }

    public int Capacity => _samples.Capacity;
    public TimeSpan MaxAge { get; }
    public int Count => _samples.Count;

    /// <summary>Newest sample, or null when empty.</summary>
    public HistorySample? Latest => _samples.Count > 0 ? _samples[_samples.Count - 1] : null;

    /// <summary>
    /// Appends a reading. Missing or non-finite values are skipped (returns false). A timestamp earlier
    /// than the newest sample means the system clock moved back, so the history restarts from it.
    /// Samples older than <see cref="MaxAge"/> before <paramref name="timeUtc"/> are dropped.
    /// </summary>
    public bool Add(DateTime timeUtc, double? value)
    {
        if (value is not double v || !double.IsFinite(v)) return false;

        if (Latest is { } latest && timeUtc < latest.TimeUtc)
            _samples.Clear();

        _samples.Add(new HistorySample(timeUtc, v));

        DateTime cutoff = timeUtc - MaxAge;
        int expired = 0;
        while (expired < _samples.Count && _samples[expired].TimeUtc < cutoff)
            expired++;
        _samples.RemoveOldest(expired);
        return true;
    }

    /// <summary>Samples no older than <paramref name="window"/> before the newest sample, oldest first.</summary>
    public IReadOnlyList<HistorySample> GetWindow(TimeSpan window)
    {
        int count = _samples.Count;
        if (count == 0) return Array.Empty<HistorySample>();

        DateTime from = _samples[count - 1].TimeUtc - (window > TimeSpan.Zero ? window : TimeSpan.Zero);
        int first = count - 1;
        while (first > 0 && _samples[first - 1].TimeUtc >= from)
            first--;

        var result = new HistorySample[count - first];
        for (int i = 0; i < result.Length; i++)
            result[i] = _samples[first + i];
        return result;
    }

    public void Clear() => _samples.Clear();
}

/// <summary>The dashboard's rolling sensor histories, recorded once per hardware poll.</summary>
public sealed class ThermalHistory
{
    public MetricHistory CpuTemperature { get; } = new();
    public MetricHistory GpuTemperature { get; } = new();
    public MetricHistory CpuLoad { get; } = new();
    public MetricHistory GpuLoad { get; } = new();

    /// <summary>CPU fan RPM, or the pump RPM on liquid-cooled systems.</summary>
    public MetricHistory CoolerRpm { get; } = new();

    /// <summary>Appends one poll; each missing reading is skipped for its own series only.</summary>
    public void Record(DateTime timeUtc, double? cpuTempC, double? gpuTempC, double? cpuLoadPct, double? gpuLoadPct, double? coolerRpm)
    {
        CpuTemperature.Add(timeUtc, cpuTempC);
        GpuTemperature.Add(timeUtc, gpuTempC);
        CpuLoad.Add(timeUtc, cpuLoadPct);
        GpuLoad.Add(timeUtc, gpuLoadPct);
        CoolerRpm.Add(timeUtc, coolerRpm);
    }

    public void Clear()
    {
        CpuTemperature.Clear();
        GpuTemperature.Clear();
        CpuLoad.Clear();
        GpuLoad.Clear();
        CoolerRpm.Clear();
    }
}
