using System.Collections.ObjectModel;
using Avalonia.Media;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

public sealed class SensorTileViewModelTests
{
    private static HeatmapTile Temp(string key, double c) =>
        new(key, key, c, HeatmapScale.Temperature, SensorHeatmap.FormatTemperature(c), "");

    [Fact]
    public void Update_ColoursFromTheSharedScale()
    {
        var tile = new SensorTileViewModel(Temp("core:1", 85));
        var load = new SensorTileViewModel(new HeatmapTile("load:x", "Total", 100, HeatmapScale.Load, "100%", "load"));

        Assert.Equal(TemperatureColorScale.ForTemperature(85), tile.Color);
        Assert.Equal(TemperatureColorScale.ForTemperature(85), Assert.IsAssignableFrom<ISolidColorBrush>(tile.AccentBrush).Color);
        Assert.IsAssignableFrom<ILinearGradientBrush>(tile.BackgroundBrush);
        Assert.Equal(TemperatureColorScale.ForLoad(100), load.Color);
        Assert.Equal("Total: 100%, load", load.AutomationName);
        Assert.Equal(81.25, tile.FillPercent, 6);
    }

    [Fact]
    public void Update_SameColour_KeepsBrushes_NewColour_ReplacesThem()
    {
        var tile = new SensorTileViewModel(Temp("core:1", 60));
        var before = tile.BackgroundBrush;

        tile.Update(Temp("core:1", 60));
        Assert.Same(before, tile.BackgroundBrush); // no restarted transition on an unchanged reading

        tile.Update(Temp("core:1", 90));
        Assert.NotSame(before, tile.BackgroundBrush);
        Assert.Equal("90°C", tile.ValueText);
    }

    [Fact]
    public void Sync_SameKeys_UpdatesTilesInPlace()
    {
        var target = new ObservableCollection<SensorTileViewModel>();
        SensorTileViewModel.Sync(target, new[] { Temp("core:1", 60), Temp("core:2", 62) });
        var first = target[0];
        int changes = 0;
        target.CollectionChanged += (_, _) => changes++;

        SensorTileViewModel.Sync(target, new[] { Temp("core:1", 75), Temp("core:2", 63) });

        Assert.Equal(0, changes);
        Assert.Same(first, target[0]);
        Assert.Equal("75°C", target[0].ValueText);
    }

    [Fact]
    public void Sync_ChangedKeys_RebuildsInNewOrderAndReusesKnownTiles()
    {
        var target = new ObservableCollection<SensorTileViewModel>();
        SensorTileViewModel.Sync(target, new[] { Temp("core:1", 60), Temp("core:2", 62) });
        var core2 = target[1];

        SensorTileViewModel.Sync(target, new[] { Temp("core:2", 64), Temp("core:3", 70) });

        Assert.Equal(new[] { "core:2", "core:3" }, target.Select(t => t.Key));
        Assert.Same(core2, target[0]);
        Assert.Equal("64°C", core2.ValueText);
    }
}
