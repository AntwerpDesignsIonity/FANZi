using System.Collections.ObjectModel;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>v4 live sync: in-place collection diffing and the shared heartbeat hub.</summary>
public sealed class LiveSyncTests
{
    private sealed record Row(int Id, string Value);

    [Fact]
    public void Sync_KeepsUnchangedRows_AndNeverResets()
    {
        var a = new Row(1, "a");
        var b = new Row(2, "b");
        var target = new ObservableCollection<Row> { a, b };
        bool reset = false;
        target.CollectionChanged += (_, e) => reset |= e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset;

        CollectionSync.Sync(target, new[] { new Row(1, "a"), new Row(2, "b") }, r => r.Id);

        Assert.Same(a, target[0]);
        Assert.Same(b, target[1]);
        Assert.False(reset);
    }

    [Fact]
    public void Sync_ReordersInsertsReplacesAndRemoves()
    {
        var target = new ObservableCollection<Row> { new(1, "a"), new(2, "b"), new(3, "c") };
        var source = new[] { new Row(3, "c"), new Row(4, "d"), new Row(1, "a2") };

        CollectionSync.Sync(target, source, r => r.Id);

        Assert.Equal(source, target);
    }

    [Fact]
    public void Sync_WithUpdater_MutatesExistingRows()
    {
        var target = new ObservableCollection<int[]> { new[] { 1, 0 }, new[] { 2, 0 } };
        var keep0 = target[0];
        CollectionSync.Sync(target, new[] { new[] { 2, 20 }, new[] { 1, 10 } }, r => r[0], (cur, next) => cur[1] = next[1]);

        Assert.Equal(2, target.Count);
        Assert.Same(keep0, target[1]);
        Assert.Equal(10, target[1][1]);
        Assert.Equal(20, target[0][1]);
    }

    [Fact]
    public void Sync_EmptySource_ClearsTarget()
    {
        var target = new ObservableCollection<Row> { new(1, "a") };
        CollectionSync.Sync(target, Array.Empty<Row>(), r => r.Id);
        Assert.Empty(target);
    }

    [Fact]
    public async Task Hub_CollectsInParallel_AndCommitsInRegistrationOrder()
    {
        var hub = new LiveSyncHub();
        var order = new List<string>();
        using var r1 = hub.Register("slow", async t => { await Task.Delay(150, t); return () => order.Add("slow"); });
        using var r2 = hub.Register("fast", t => Task.FromResult<Action?>(() => order.Add("fast")));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var applies = await hub.CollectAsync(CancellationToken.None);
        sw.Stop();
        Assert.False(hub.IsDriven);
        hub.Commit(applies);

        Assert.Equal(new[] { "slow", "fast" }, order);
        Assert.True(hub.IsDriven);
        Assert.Equal(1, hub.CycleCount);
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public async Task Hub_SkipsFailingCollector_AndUnregistersOnDispose()
    {
        var hub = new LiveSyncHub();
        int applied = 0;
        var bad = hub.Register("bad", _ => throw new InvalidOperationException("boom"));
        using var good = hub.Register("good", _ => Task.FromResult<Action?>(() => applied++));

        hub.Commit(await hub.CollectAsync(CancellationToken.None));
        Assert.Equal(1, applied);

        bad.Dispose();
        good.Dispose();
        Assert.Empty(await hub.CollectAsync(CancellationToken.None));
    }

    [Fact]
    public void SystemMemory_ReadsPlausibleValues()
    {
        if (!OperatingSystem.IsWindows()) return;
        var r = SystemMemory.Read();
        Assert.NotNull(r);
        Assert.InRange(r!.Value.UsedPercent, 0, 100);
        Assert.True(r.Value.TotalGb > 0.5);
        Assert.Contains("GB", r.Value.Display);
    }
}
