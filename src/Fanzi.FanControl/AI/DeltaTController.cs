using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

/// <summary>
/// Multi-sensor fusion / Delta-T (ΔT) airflow controller.
///
/// Traditional fan controllers map one sensor to one fan. Problem: when the GPU dumps
/// 300W of heat into the case but the CPU is idle, the intake/exhaust fans stay quiet
/// and the case becomes a heat soak.
///
/// This controller calculates ΔT = (CaseInternalTemp - AmbientRoomTemp) and ramps
/// case airflow proportional to ΔT. It can also fuse CPU + GPU temps as a "heat-load"
/// signal that responds to *any* heat source, not just one.
///
/// Typical ΔT targets:
///   - ΔT &lt; 10°C  → low airflow (case is cool relative to room)
///   - ΔT 10-20°C  → moderate airflow
///   - ΔT 20-30°C  → high airflow (case is heat-soaked)
///   - ΔT &gt; 30°C  → max airflow (something is overheating)
/// </summary>
public sealed class DeltaTController
{
    /// <summary>Reference ambient room temperature (typically 22°C). Override if you measure it.</summary>
    public double AmbientTempC { get; set; } = 22.0;

    /// <summary>ΔT setpoints — below LowTarget = silent, above HighTarget = max airflow.</summary>
    public double LowTargetDeltaT  { get; set; } = 10.0;
    public double HighTargetDeltaT { get; set; } = 30.0;

    /// <summary>Min/max fan % the controller will output.</summary>
    public double MinFanPercent { get; set; } = 20.0;
    public double MaxFanPercent { get; set; } = 100.0;

    /// <summary>How heavily to weight GPU heat in the fused signal (0..1).</summary>
    public double GpuWeight { get; set; } = 0.5;

    /// <summary>
    /// Compute recommended case (intake/exhaust) fan % from multi-sensor inputs.
    /// </summary>
    /// <param name="caseInternalTempC">Motherboard / case temperature sensor (if available).</param>
    /// <param name="cpuTempC">CPU temperature.</param>
    /// <param name="gpuTempC">GPU temperature.</param>
    public DeltaTResult Compute(double? caseInternalTempC, double? cpuTempC, double? gpuTempC)
    {
        // Heat-load fusion: max(CPU, weighted-GPU, case)
        var samples = new List<double>();
        if (caseInternalTempC.HasValue) samples.Add(caseInternalTempC.Value);
        if (cpuTempC.HasValue) samples.Add(cpuTempC.Value);
        if (gpuTempC.HasValue) samples.Add(cpuTempC.HasValue
            ? cpuTempC.Value * (1 - GpuWeight) + gpuTempC.Value * GpuWeight
            : gpuTempC.Value);

        if (samples.Count == 0)
        {
            return new DeltaTResult(0, MinFanPercent, "no sensors");
        }

        double effectiveTempC = samples.Max();
        double deltaT = effectiveTempC - AmbientTempC;

        // Linear interpolation between Low → High targets
        double fanPct;
        if (deltaT <= LowTargetDeltaT)
        {
            fanPct = MinFanPercent;
        }
        else if (deltaT >= HighTargetDeltaT)
        {
            fanPct = MaxFanPercent;
        }
        else
        {
            double normalized = (deltaT - LowTargetDeltaT) / (HighTargetDeltaT - LowTargetDeltaT);
            fanPct = MinFanPercent + normalized * (MaxFanPercent - MinFanPercent);
        }

        string reason = $"ΔT={deltaT:F1}°C ({effectiveTempC:F0}-{AmbientTempC:F0}) → {fanPct:F0}%";
        return new DeltaTResult(deltaT, fanPct, reason);
    }
}

public sealed record DeltaTResult(double DeltaT, double SuggestedFanPercent, string Explanation);
