using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Lightweight Task-Manager-style process monitor. Samples CPU time twice with a delay
/// to derive per-process CPU%, plus memory + thread + handle counts.
/// </summary>
public sealed class ProcessMonitorService
{
    private readonly Dictionary<int, (DateTime Ts, TimeSpan CpuTime)> _lastSample = new();
    private static readonly int LogicalCpuCount = Environment.ProcessorCount;

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<ProcessInfo>>(() =>
        {
            var now = DateTime.UtcNow;
            var list = new List<ProcessInfo>();

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.HasExited) continue;
                    var cpuTime = p.TotalProcessorTime;

                    double cpuPct = 0;
                    if (_lastSample.TryGetValue(p.Id, out var prev))
                    {
                        double secondsElapsed = (now - prev.Ts).TotalSeconds;
                        double cpuUsedSec = (cpuTime - prev.CpuTime).TotalSeconds;
                        if (secondsElapsed > 0)
                            cpuPct = Math.Clamp(cpuUsedSec / (secondsElapsed * LogicalCpuCount) * 100.0, 0, 100);
                    }
                    _lastSample[p.Id] = (now, cpuTime);

                    list.Add(new ProcessInfo(
                        Pid: p.Id,
                        Name: p.ProcessName,
                        Description: SafeGetDescription(p),
                        CpuPercent: cpuPct,
                        MemoryMb: p.WorkingSet64 / 1024.0 / 1024.0,
                        Threads: p.Threads.Count,
                        Handles: p.HandleCount,
                        StartTime: SafeGetStartTime(p)));
                }
                catch { /* access denied or process died — skip */ }
            }

            // Drop dead PIDs from sample cache
            var alive = list.Select(p => p.Pid).ToHashSet();
            foreach (var pid in _lastSample.Keys.ToList())
                if (!alive.Contains(pid)) _lastSample.Remove(pid);

            return (IReadOnlyList<ProcessInfo>)list
                .OrderByDescending(p => p.CpuPercent)
                .ThenByDescending(p => p.MemoryMb)
                .ToList();
        }, ct);
    }

    public bool KillProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return p.WaitForExit(3000);
        }
        catch { return false; }
    }

    public bool SetPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = priority;
            return true;
        }
        catch { return false; }
    }

    private static string SafeGetDescription(Process p)
    {
        try { return p.MainWindowTitle?.Length > 0 ? p.MainWindowTitle : ""; }
        catch { return ""; }
    }

    private static DateTime? SafeGetStartTime(Process p)
    {
        try { return p.StartTime; }
        catch { return null; }
    }
}

public sealed record ProcessInfo(
    int Pid,
    string Name,
    string Description,
    double CpuPercent,
    double MemoryMb,
    int Threads,
    int Handles,
    DateTime? StartTime);
