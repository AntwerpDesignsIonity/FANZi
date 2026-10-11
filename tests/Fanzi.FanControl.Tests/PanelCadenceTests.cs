using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

/// <summary>v4.0.2: Tasks / Network / Power panels sync every 0.5 s (in-place keyed diffs, no Clear()+Add()).</summary>
public class PanelCadenceTests
{
    [Fact]
    public void PanelsAndLiveSyncLoop_RunAtHalfASecond()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), LiveSyncHub.PanelInterval);
        Assert.Equal(LiveSyncHub.PanelInterval, MainWindowViewModel.LiveSyncInterval);
        Assert.True(MainWindowViewModel.LiveSyncHiddenInterval > MainWindowViewModel.LiveSyncInterval);
    }

    [Fact]
    public async Task HalfSecondCollector_IsDueEveryHalfSecondTick()
    {
        var hub = new LiveSyncHub();
        int runs = 0;
        using var _ = hub.Register("Panel", _ => { Interlocked.Increment(ref runs); return Task.FromResult<Action?>(null); },
            LiveSyncHub.PanelInterval);

        await hub.CollectAsync(CancellationToken.None);
        await hub.CollectAsync(CancellationToken.None); // immediately again: not due yet
        Assert.Equal(1, runs);

        await Task.Delay(LiveSyncHub.PanelInterval);
        await hub.CollectAsync(CancellationToken.None);
        Assert.Equal(2, runs);
    }
}
