using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.ViewModels;

/// <summary>Sensors-tab heatmap: per-core CPU tiles, CPU summary tiles and GPU tiles, refreshed every poll.</summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<SensorTileViewModel> CpuCoreTiles { get; } = new();
    public ObservableCollection<SensorTileViewModel> CpuSummaryTiles { get; } = new();
    public ObservableCollection<SensorTileViewModel> GpuTiles { get; } = new();

    [ObservableProperty] private bool _hasCpuCoreTiles;
    [ObservableProperty] private bool _hasGpuTiles;
    [ObservableProperty] private string _cpuHeatmapCaption = "Waiting for sensors…";

    private void UpdateHeatmap(IReadOnlyList<CpuReadingSnapshot> cpu, IReadOnlyList<GpuReadingSnapshot> gpu)
    {
        var cpuReadings = cpu.Select(r => (r.Name, r.Value, r.Kind)).ToList();
        var cores = SensorHeatmap.BuildCpuCores(cpuReadings);
        SensorTileViewModel.Sync(CpuCoreTiles, cores);
        SensorTileViewModel.Sync(CpuSummaryTiles, SensorHeatmap.BuildCpuSummary(cpuReadings));
        SensorTileViewModel.Sync(GpuTiles, SensorHeatmap.BuildGpu(gpu.Select(r => (r.Name, r.Value, r.Kind))));

        HasCpuCoreTiles = cores.Count > 0;
        HasGpuTiles = GpuTiles.Count > 0;
        CpuHeatmapCaption = cores.Count == 0
            ? "No per-core sensors exposed"
            : cores.All(c => c.Scale == HeatmapScale.Temperature)
                ? $"{cores.Count} cores · coloured by temperature"
                : $"{cores.Count} cores · coloured by load (no per-core temperature sensors)";
    }
}
