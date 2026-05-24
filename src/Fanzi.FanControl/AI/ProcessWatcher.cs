using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fanzi.FanControl.AI;

public sealed class ProcessWatcher
{
    private readonly HashSet<string> _watchedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastDetectedProcess;

    public string? ActiveMatchedProcess => _lastDetectedProcess;

    public void SetWatchList(IEnumerable<string> processNames)
    {
        _watchedProcesses.Clear();
        foreach (var name in processNames)
            _watchedProcesses.Add(name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase));
    }

    public string? CheckRunningProcesses()
    {
        if (_watchedProcesses.Count == 0)
        {
            _lastDetectedProcess = null;
            return null;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _lastDetectedProcess = null;
            return null;
        }

        _lastDetectedProcess = CheckWindowsProcesses();
        return _lastDetectedProcess;
    }

    [SupportedOSPlatform("windows")]
    private string? CheckWindowsProcesses()
    {
        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    if (_watchedProcesses.Contains(proc.ProcessName))
                        return proc.ProcessName;
                }
                catch
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static IReadOnlyList<string> GetTopCpuProcessNames(int count = 5)
    {
        try
        {
            return Process.GetProcesses()
                .OrderByDescending(p =>
                {
                    try { return p.WorkingSet64; }
                    catch { return 0L; }
                })
                .Take(count)
                .Select(p =>
                {
                    try { return p.ProcessName; }
                    catch { return "unknown"; }
                    finally { p.Dispose(); }
                })
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
