using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// One heartbeat for every live panel (v4). Each panel registers a <em>collector</em> that gathers its
/// data off the UI thread and returns an <em>apply</em> action. The dashboard refresh loop runs all
/// collectors in parallel with the hardware poll and then applies every result in the same UI-thread
/// pass, so the dashboard, Tasks, Network, Power and the overlay all change together ("all in one
/// update sync") instead of on four unrelated timers.
/// <para>
/// Panels keep a slow fallback timer of their own: when nothing drives the hub (unit tests, or the
/// dashboard loop paused) <see cref="IsDriven"/> turns false after <see cref="DrivenTimeout"/> and the
/// fallback refreshes them as before.
/// </para>
/// </summary>
public sealed class LiveSyncHub
{
    /// <summary>Process-wide hub the dashboard loop drives.</summary>
    public static LiveSyncHub Shared { get; } = new();

    /// <summary>
    /// Cadence of the Tasks / Network / Power panels (v4.0.2: 0.5 s, was 2 s / every poll / 3 s). Their lists
    /// are updated in place by <see cref="CollectionSync"/> keyed diffs, so 2 Hz moves rows smoothly.
    /// </summary>
    public static readonly TimeSpan PanelInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>A collector slower than this is skipped for the cycle (its previous data stays on screen).</summary>
    internal static readonly TimeSpan CollectorTimeout = TimeSpan.FromSeconds(2.5);

    /// <summary>Without a cycle for this long, panels fall back to their own timer.</summary>
    internal static readonly TimeSpan DrivenTimeout = TimeSpan.FromSeconds(12);

    private readonly object _sync = new();
    private readonly List<Registration> _registrations = new();
    private long _lastCycleTicks;
    private long _cycle;

    /// <summary>Raised on the caller's thread (UI) right after a cycle's results were applied.</summary>
    public event EventHandler<long>? Committed;

    /// <summary>Number of completed cycles (diagnostics and tests).</summary>
    public long CycleCount => Interlocked.Read(ref _cycle);

    /// <summary>True while the dashboard loop has cycled the hub recently.</summary>
    public bool IsDriven
    {
        get
        {
            long last = Interlocked.Read(ref _lastCycleTicks);
            return last != 0 && Stopwatch.GetElapsedTime(last) < DrivenTimeout;
        }
    }

    /// <summary>
    /// Registers a collector; dispose the result to unregister. <paramref name="collect"/> runs on the thread
    /// pool and returns the action that pushes its data into bound properties (run on the UI thread), or null
    /// when there is nothing to apply.
    /// </summary>
    public IDisposable Register(string name, Func<CancellationToken, Task<Action?>> collect, TimeSpan? minInterval = null)
    {
        ArgumentNullException.ThrowIfNull(collect);
        var registration = new Registration(this, name, collect) { MinInterval = minInterval ?? TimeSpan.Zero };
        lock (_sync) _registrations.Add(registration);
        return registration;
    }

    /// <summary>
    /// Runs every collector in parallel (each capped at <see cref="CollectorTimeout"/>) and returns the apply
    /// actions in registration order. Never throws for a failing collector; it is logged once and skipped.
    /// </summary>
    public async Task<IReadOnlyList<Action>> CollectAsync(CancellationToken cancellationToken)
    {
        Registration[] regs;
        lock (_sync) regs = _registrations.Where(r => r.IsDue).ToArray();
        if (regs.Length == 0) return Array.Empty<Action>();
        foreach (var r in regs) r.LastRunTicks = Stopwatch.GetTimestamp();

        var tasks = regs.Select(r => RunOneAsync(r, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(a => a is not null).Cast<Action>().ToArray();
    }

    /// <summary>Applies one cycle's actions (call on the UI thread) and raises <see cref="Committed"/>.</summary>
    public void Commit(IReadOnlyList<Action> applies)
    {
        foreach (var apply in applies)
        {
            try { apply(); }
            catch (Exception ex) { AppLog.Warn("Live sync: applying a panel update failed", ex); }
        }

        Interlocked.Exchange(ref _lastCycleTicks, Stopwatch.GetTimestamp());
        long n = Interlocked.Increment(ref _cycle);
        Committed?.Invoke(this, n);
    }

    private static async Task<Action?> RunOneAsync(Registration r, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CollectorTimeout);
            var work = Task.Run(() => r.Collect(timeout.Token), timeout.Token);
            var finished = await Task.WhenAny(work, Task.Delay(CollectorTimeout, cancellationToken)).ConfigureAwait(false);
            if (finished != work)
            {
                // Observe the late task so a failure there never becomes an unobserved exception.
                _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                return null;
            }
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (!r.FailureLogged)
            {
                r.FailureLogged = true;
                AppLog.Warn($"Live sync: collector '{r.Name}' failed", ex);
            }
            return null;
        }
    }

    private void Unregister(Registration registration)
    {
        lock (_sync) _registrations.Remove(registration);
    }

    private sealed class Registration : IDisposable
    {
        private readonly LiveSyncHub _owner;

        public Registration(LiveSyncHub owner, string name, Func<CancellationToken, Task<Action?>> collect)
        {
            _owner = owner;
            Name = name;
            Collect = collect;
        }

        public string Name { get; }
        public Func<CancellationToken, Task<Action?>> Collect { get; }
        public bool FailureLogged { get; set; }

        /// <summary>Heavier panels (process list, power) refresh less often than the 1 s dashboard tick.</summary>
        public TimeSpan MinInterval { get; init; }
        public long LastRunTicks { get; set; }

        // 50 ms slack so a 2 s collector is not skipped by timer jitter on a 1 s tick.
        public bool IsDue => LastRunTicks == 0
            || Stopwatch.GetElapsedTime(LastRunTicks) >= MinInterval - TimeSpan.FromMilliseconds(50);

        public void Dispose() => _owner.Unregister(this);
    }
}
