using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>
/// One Sensors-tab heatmap tile. Tiles are updated in place across polls (see <see cref="Sync"/>) so the
/// theme's BrushTransitions animate the colour change instead of the tile being re-created.
/// </summary>
public sealed partial class SensorTileViewModel : ObservableObject
{
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _valueText = "--";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDetail))] private string _detail = string.Empty;
    [ObservableProperty] private double _fillPercent;
    [ObservableProperty] private string _automationName = string.Empty;
    [ObservableProperty] private IBrush _accentBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush _valueBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush _backgroundBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush _borderBrush = Brushes.Transparent;

    private Color? _color;

    public SensorTileViewModel(HeatmapTile tile)
    {
        Key = tile.Key;
        Update(tile);
    }

    public string Key { get; }

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Scale colour the brushes were built from (tests / render checks).</summary>
    public Color Color => _color ?? default;

    public void Update(HeatmapTile tile)
    {
        Label = tile.Label;
        ValueText = tile.ValueText;
        Detail = tile.Detail;
        FillPercent = tile.Fill * 100;
        AutomationName = tile.Detail.Length > 0 ? $"{tile.Label}: {tile.ValueText}, {tile.Detail}" : $"{tile.Label}: {tile.ValueText}";

        Color color = tile.Scale == HeatmapScale.Temperature
            ? TemperatureColorScale.ForTemperature(tile.Value)
            : TemperatureColorScale.ForLoad(tile.Value);
        if (_color == color) return;
        _color = color;

        // New brush instances (same shape every time) so BrushTransition interpolates old → new.
        AccentBrush = new ImmutableSolidColorBrush(color);
        ValueBrush = new ImmutableSolidColorBrush(TemperatureColorScale.Lighten(color, 0.3));
        BorderBrush = new ImmutableSolidColorBrush(TemperatureColorScale.WithAlpha(color, 0.55));
        BackgroundBrush = new ImmutableLinearGradientBrush(
            new[]
            {
                new ImmutableGradientStop(0, TemperatureColorScale.WithAlpha(color, 0.30)),
                new ImmutableGradientStop(1, TemperatureColorScale.WithAlpha(color, 0.07)),
            },
            startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
            endPoint: new RelativePoint(1, 1, RelativeUnit.Relative));
    }

    /// <summary>
    /// Brings <paramref name="target"/> in line with <paramref name="tiles"/>: when the keys are unchanged
    /// (the normal poll) every tile is updated in place; otherwise the list is rebuilt.
    /// </summary>
    public static void Sync(ObservableCollection<SensorTileViewModel> target, IReadOnlyList<HeatmapTile> tiles)
    {
        if (target.Count == tiles.Count && target.Select(t => t.Key).SequenceEqual(tiles.Select(t => t.Key)))
        {
            for (int i = 0; i < tiles.Count; i++)
                target[i].Update(tiles[i]);
            return;
        }

        var existing = target.ToDictionary(t => t.Key);
        target.Clear();
        foreach (var tile in tiles)
        {
            if (existing.Remove(tile.Key, out var vm))
                vm.Update(tile);
            else
                vm = new SensorTileViewModel(tile);
            target.Add(vm);
        }
    }
}
