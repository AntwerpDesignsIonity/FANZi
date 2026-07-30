using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Port scanner service — scans TCP ports on localhost or remote hosts.
/// Reports open/closed/filtered status with process ownership where available.
/// </summary>
public sealed class PortScannerService
{
    public async Task<IReadOnlyList<PortScanResult>> ScanAsync(
        string host, int startPort, int endPort, int timeoutMs = 200,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<PortScanResult>();
        var semaphore = new SemaphoreSlim(50); // limit concurrent connections

        var tasks = new List<Task>();
        for (int port = startPort; port <= endPort; port++)
        {
            ct.ThrowIfCancellationRequested();
            int p = port;
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var result = await ScanPortAsync(host, p, timeoutMs, ct);
                    results.Add(result);
                }
                finally { semaphore.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.Port).ToList();
    }

    public async Task<PortScanResult> ScanPortAsync(string host, int port, int timeoutMs = 300, CancellationToken ct = default)
    {
        var state = PortState.Closed;
        string? processName = null;
        int? processId = null;

        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, ct));

            if (completed == connectTask && connectTask.IsCompletedSuccessfully && client.Connected)
            {
                state = PortState.Open;
                // Try to find owning process via IP global properties
                (processName, processId) = FindOwningProcess(port);
            }
            else
            {
                state = PortState.Filtered;
            }
        }
        catch
        {
            state = PortState.Closed;
        }

        return new PortScanResult(port, state, processName, processId);
    }

    public IReadOnlyList<PortScanResult> GetListeningPorts()
    {
        var results = new List<PortScanResult>();
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();

            foreach (var endpoint in properties.GetActiveTcpListeners())
            {
                var (name, pid) = FindOwningProcess(endpoint.Port);
                results.Add(new PortScanResult(endpoint.Port, PortState.Open, name, pid));
            }

            foreach (var endpoint in properties.GetActiveUdpListeners())
            {
                var (name, pid) = FindOwningProcess(endpoint.Port);
                results.Add(new PortScanResult(endpoint.Port, PortState.Open, name, pid));
            }
        }
        catch { }

        return results.OrderBy(r => r.Port).DistinctBy(r => r.Port).ToList();
    }

    public bool ClosePort(int port)
    {
        // Find and kill the process owning this port
        var (name, pid) = FindOwningProcess(port);
        if (pid.HasValue && pid.Value > 0)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid.Value);
                p.Kill(entireProcessTree: true);
                return p.WaitForExit(3000);
            }
            catch { }
        }
        return false;
    }

    private static (string? Name, int? Pid) FindOwningProcess(int port)
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var connections = properties.GetActiveTcpConnections();
            foreach (var conn in connections)
            {
                if (conn.LocalEndPoint.Port == port)
                {
                    // We can't directly get PID from TcpConnectionInformation
                    // Use netstat as fallback
                    break;
                }
            }

            // Fallback: use IPGlobalProperties to match
            var listeners = properties.GetActiveTcpListeners();
            foreach (var ep in listeners)
            {
                if (ep.Port == port)
                {
                    // Try to find by scanning processes
                    foreach (var p in System.Diagnostics.Process.GetProcesses())
                    {
                        try
                        {
                            if (p.HasExited) continue;
                            // Heuristic: match by process name patterns for common services
                            string pname = p.ProcessName.ToLowerInvariant();
                            if (IsCommonPortService(port, pname))
                                return (p.ProcessName, p.Id);
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                    break;
                }
            }
        }
        catch { }

        return (null, null);
    }

    private static bool IsCommonPortService(int port, string processName)
    {
        return port switch
        {
            80 => processName.Contains("http") || processName.Contains("nginx") || processName.Contains("apache"),
            443 => processName.Contains("http") || processName.Contains("nginx") || processName.Contains("apache"),
            3389 => processName.Contains("svchost") || processName.Contains("rdp"),
            6742 => processName.Contains("openrgb"),
            _ => false,
        };
    }
}

public enum PortState { Open, Closed, Filtered }

public sealed record PortScanResult(int Port, PortState State, string? ProcessName, int? ProcessId)
{
    public string StateDisplay => State switch
    {
        PortState.Open => "🟢 OPEN",
        PortState.Closed => "⚫ CLOSED",
        PortState.Filtered => "🟡 FILTERED",
        _ => "UNKNOWN"
    };

    public string ProcessDisplay => ProcessName is not null
        ? $"{ProcessName} (PID {ProcessId})"
        : "Unknown";
}
