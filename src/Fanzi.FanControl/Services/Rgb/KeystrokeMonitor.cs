using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>
/// Reports key presses system-wide for Typing FX. Windows: polls <c>GetAsyncKeyState</c> for all 256 virtual keys
/// every 8 ms on a background thread — the "pressed since last call" bit catches even taps shorter than a poll —
/// so no low-level hook, no message pump and no DLL injection. Only key <em>codes</em> are observed, never text,
/// and nothing leaves the process (the codes drive the light show and are dropped). Other OSes: inert.
/// </summary>
public sealed class KeystrokeMonitor : IDisposable
{
    /// <summary>Raised on the polling thread: virtual-key code of the key that went down.</summary>
    public event Action<int>? KeyDown;

    private Thread? _thread;
    private volatile bool _running;
    private readonly bool[] _down = new bool[256];

    public bool IsSupported => OperatingSystem.IsWindows();
    public bool IsRunning => _running;

    /// <summary>Keys seen since Start (for the UI counter).</summary>
    public long KeyCount => Interlocked.Read(ref _count);
    private long _count;

    public void Start()
    {
        if (!IsSupported || _running) return;
        _running = true;
        _thread = new Thread(Poll) { IsBackground = true, Name = "FANZi Typing FX", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread = null;
        Array.Clear(_down);
    }

    private void Poll()
    {
        // Prime: swallow the "pressed since last call" flags accumulated before we started.
        for (int vk = 1; vk < 256; vk++) _ = GetAsyncKeyState(vk);

        while (_running)
        {
            for (int vk = 8; vk < 256; vk++)
            {
                // Skip mouse buttons (1–6) and the modifier aliases 0x10–0x12 (the L/R codes are reported too).
                if (vk is 0x10 or 0x11 or 0x12) continue;
                short state = GetAsyncKeyState(vk);
                bool isDown = (state & 0x8000) != 0;
                bool pressedSince = (state & 0x0001) != 0;
                if ((isDown && !_down[vk]) || (pressedSince && !isDown))
                {
                    Interlocked.Increment(ref _count);
                    try { KeyDown?.Invoke(vk); } catch { }
                }
                _down[vk] = isDown;
            }
            Thread.Sleep(8);
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void Dispose() => Stop();
}
