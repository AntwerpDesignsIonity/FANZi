using Fanzi.FanControl.Models;
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public sealed class HardwareMonitorService : IHardwareMonitorService
{
    private readonly object _syncRoot = new();
    private readonly Computer? _computer;
    private readonly Dictionary<string, IControl> _controlMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public HardwareMonitorService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsBatteryEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
            IsPowerMonitorEnabled = false,
        };

        _computer.Open();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_syncRoot)
        {
            _controlMap.Clear();
            _computer?.Close();
        }
    }

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows() || _computer is null)
        {
            return Task.FromResult(new HardwareSnapshot(
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                Array.Empty<CpuReadingSnapshot>(),
                Array.Empty<GpuReadingSnapshot>(),
                Array.Empty<FanChannelSnapshot>(),
                "Hardware access is only enabled on Windows. The UI still runs cross-platform, but fan telemetry requires Windows sensor support.",
                null,
                null,
                null,
                null));
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            List<ISensor> sensors = new();
            string? cpuName = null;
            string? gpuName = null;
            foreach (IHardware hardware in _computer.Hardware)
            {
                CollectSensors(hardware, sensors);
                if (hardware.HardwareType == HardwareType.Cpu && cpuName is null)
                {
                    cpuName = hardware.Name;
                }
                else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && gpuName is null)
                {
                    gpuName = hardware.Name;
                }
            }

            List<double> cpuTemperatures = sensors
                .Where(sensor => sensor.SensorType == SensorType.Temperature && IsCpuTemperature(sensor))
                .Select(sensor => (double?)sensor.Value)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();

            double? cpuPackageTemperature = sensors
                .Where(sensor => sensor.SensorType == SensorType.Temperature && IsCpuTemperature(sensor) && sensor.Name.Contains("package", StringComparison.OrdinalIgnoreCase))
                .Select(sensor => (double?)sensor.Value)
                .FirstOrDefault(value => value.HasValue);

            if (!cpuPackageTemperature.HasValue && cpuTemperatures.Count > 0)
            {
                cpuPackageTemperature = cpuTemperatures.Max();
            }

            double? cpuTotalLoadPercent = SelectCpuMetric(
                sensors,
                SensorType.Load,
                name => name.Contains("total", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("package", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("cpu total", StringComparison.OrdinalIgnoreCase));

            double? cpuAverageClockMhz = AverageCpuMetric(
                sensors,
                SensorType.Clock,
                name => name.Contains("core", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("effective", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("clock", StringComparison.OrdinalIgnoreCase));

            double? cpuPackagePowerWatts = SelectCpuMetric(
                sensors,
                SensorType.Power,
                name => name.Contains("package", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("cpu", StringComparison.OrdinalIgnoreCase));

            double? cpuCoreVoltage = SelectCpuMetric(
                sensors,
                SensorType.Voltage,
                name => name.Contains("vcore", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("core", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("cpu", StringComparison.OrdinalIgnoreCase));

            List<double> gpuTemperatures = sensors
                .Where(sensor => IsGpuMetricSensor(sensor, SensorType.Temperature))
                .Select(sensor => (double?)sensor.Value)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();

            double? gpuCoreTemperature = SelectGpuMetric(
                sensors,
                SensorType.Temperature,
                name => name.Contains("core", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("edge", StringComparison.OrdinalIgnoreCase));

            if (!gpuCoreTemperature.HasValue && gpuTemperatures.Count > 0)
            {
                gpuCoreTemperature = gpuTemperatures.FirstOrDefault();
            }

            double? gpuHotspotTemperature = SelectGpuMetric(
                sensors,
                SensorType.Temperature,
                name => name.Contains("hotspot", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("junction", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("memory", StringComparison.OrdinalIgnoreCase));

            if (!gpuHotspotTemperature.HasValue && gpuTemperatures.Count > 0)
            {
                gpuHotspotTemperature = gpuTemperatures.Max();
            }

            double? gpuLoadPercent = SelectGpuMetric(
                sensors,
                SensorType.Load,
                name => name.Contains("core", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("total", StringComparison.OrdinalIgnoreCase));

            double? gpuCoreClockMhz = SelectGpuMetric(
                sensors,
                SensorType.Clock,
                name => name.Contains("core", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("graphics", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("gpu", StringComparison.OrdinalIgnoreCase));

            double? gpuPowerWatts = SelectGpuMetric(
                sensors,
                SensorType.Power,
                name => name.Contains("package", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("board", StringComparison.OrdinalIgnoreCase));

            double? gpuMemoryUsedMb = SelectGpuMetric(
                sensors,
                SensorType.SmallData,
                name => (name.Contains("memory", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("vram", StringComparison.OrdinalIgnoreCase))
                    && name.Contains("used", StringComparison.OrdinalIgnoreCase));

            double? gpuMemoryTotalMb = SelectGpuMetric(
                sensors,
                SensorType.SmallData,
                name => (name.Contains("memory", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("vram", StringComparison.OrdinalIgnoreCase))
                    && (name.Contains("total", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("free", StringComparison.OrdinalIgnoreCase) == false));

            List<FanChannelSnapshot> fanSnapshots = BuildFanSnapshots(sensors);
            FanChannelSnapshot? cpuFan = fanSnapshots.FirstOrDefault(IsCpuFanSnapshot);

            // Fallback: if no fan matched CPU keywords but exactly one fan exists, use it.
            if (cpuFan is null && fanSnapshots.Count == 1)
            {
                cpuFan = fanSnapshots[0];
            }

            List<CpuReadingSnapshot> cpuReadings = BuildCpuReadings(sensors);
            List<GpuReadingSnapshot> gpuReadings = BuildGpuReadings(sensors);

            if (cpuFan is not null)
            {
                fanSnapshots = fanSnapshots
                    .OrderByDescending(fan => fan.Id == cpuFan.Id)
                    .ThenBy(fan => fan.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            string statusMessage = fanSnapshots.Count switch
            {
                0 when cpuTemperatures.Count == 0 => "No CPU temperature or fan sensors were exposed. Try running elevated or confirm that your motherboard is supported by LibreHardwareMonitor.",
                0 => "CPU temperatures are available, but no fan channels were exposed by the motherboard or attached controller.",
                _ when cpuFan is null => "Fan telemetry is live, but a dedicated CPU fan header was not clearly identified. Generic fan channels are still shown below.",
                _ when fanSnapshots.All(fan => !fan.CanControl) => "Fan telemetry is active. Manual fan control is disabled because this hardware did not expose software-controllable channels.",
                _ => "Telemetry is live. Apply sends a manual duty target; Auto restores the device default or BIOS-controlled mode for supported channels."
            };

            return Task.FromResult(new HardwareSnapshot(
                DateTimeOffset.UtcNow,
                cpuPackageTemperature,
                cpuTemperatures.Count == 0 ? null : cpuTemperatures.Average(),
                cpuTemperatures.Count == 0 ? null : cpuTemperatures.Max(),
                cpuTotalLoadPercent,
                cpuAverageClockMhz,
                cpuPackagePowerWatts,
                cpuCoreVoltage,
                gpuCoreTemperature,
                gpuHotspotTemperature,
                gpuLoadPercent,
                gpuCoreClockMhz,
                gpuPowerWatts,
                cpuFan,
                cpuReadings,
                gpuReadings,
                fanSnapshots,
                statusMessage,
                cpuName,
                gpuName,
                gpuMemoryUsedMb,
                gpuMemoryTotalMb));
        }
    }

    public Task<SetFanControlResult> SetFanControlAsync(string channelId, double percent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (!_controlMap.TryGetValue(channelId, out IControl? control))
            {
                return Task.FromResult(new SetFanControlResult(false, "That fan channel is not currently controllable."));
            }

            float clamped = (float)Math.Clamp(percent, control.MinSoftwareValue, control.MaxSoftwareValue);
            control.SetSoftware(clamped);
            control.Sensor?.Hardware.Update();

            return Task.FromResult(new SetFanControlResult(true, $"Manual control applied at {clamped.ToString("F0", CultureInfo.InvariantCulture)}%."));
        }
    }

    public Task<SetFanControlResult> RestoreAutomaticControlAsync(string channelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (!_controlMap.TryGetValue(channelId, out IControl? control))
            {
                return Task.FromResult(new SetFanControlResult(false, "That fan channel does not expose a default control mode."));
            }

            control.SetDefault();
            control.Sensor?.Hardware.Update();

            return Task.FromResult(new SetFanControlResult(true, "Automatic or BIOS control restored for this channel."));
        }
    }

    private List<FanChannelSnapshot> BuildFanSnapshots(List<ISensor> sensors)
    {
        _controlMap.Clear();

        List<ISensor> fanSensors = sensors.Where(sensor => sensor.SensorType == SensorType.Fan).ToList();
        List<ISensor> controlSensors = sensors.Where(sensor => sensor.Control is not null).ToList();
        List<FanChannelSnapshot> fanSnapshots = new();
        HashSet<string> claimedControlIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (ISensor fanSensor in fanSensors)
        {
            ISensor? controlSensor = FindBestControlSensor(fanSensor, controlSensors);
            if (controlSensor?.Control is not null)
            {
                claimedControlIds.Add(controlSensor.Control.Identifier.ToString());
            }

            fanSnapshots.Add(CreateFanSnapshot(fanSensor, controlSensor));
        }

        foreach (ISensor controlSensor in controlSensors.Where(sensor => sensor.Control is not null))
        {
            string controlId = controlSensor.Control!.Identifier.ToString();
            if (claimedControlIds.Contains(controlId))
            {
                continue;
            }

            fanSnapshots.Add(CreateFanSnapshot(null, controlSensor));
        }

        return fanSnapshots
            .OrderBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private FanChannelSnapshot CreateFanSnapshot(ISensor? fanSensor, ISensor? controlSensor)
    {
        IControl? control = controlSensor?.Control;
        string id = control?.Identifier.ToString()
            ?? fanSensor?.Identifier.ToString()
            ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        if (control is not null)
        {
            _controlMap[id] = control;
        }

        string name = fanSensor?.Name
            ?? controlSensor?.Name.Replace("Control", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()
            ?? "Unnamed channel";

        if (controlSensor is not null && controlSensor.SensorType == SensorType.Control && !name.Contains("fan", StringComparison.OrdinalIgnoreCase) && fanSensor is null)
        {
            name = $"{name} fan";
        }

        double? currentControlPercent = null;
        if (controlSensor?.Value is float controlValue)
        {
            currentControlPercent = controlValue;
        }
        else if (control is not null && control.ControlMode == ControlMode.Software)
        {
            currentControlPercent = control.SoftwareValue;
        }

        FanDeviceKind deviceKind = ClassifyDevice(name);

        string capabilityMessage = control switch
        {
            null => deviceKind == FanDeviceKind.Fan
                ? "Read-only sensor channel. The device did not expose a writable software control endpoint."
                : $"Read-only {deviceKind.ToString().ToLowerInvariant()} channel. Pump and AIO headers are typically BIOS-managed.",
            _ when control.ControlMode == ControlMode.Software => $"Manual software control available between {control.MinSoftwareValue:F0}% and {control.MaxSoftwareValue:F0}%.",
            _ => "Software control available. Auto/BIOS mode is currently active until you apply a manual value."
        };

        string hardwareSource = fanSensor?.Hardware.Name
            ?? controlSensor?.Hardware.Name
            ?? "Unknown";

        int sensorIndex = fanSensor?.Index
            ?? controlSensor?.Index
            ?? -1;

        return new FanChannelSnapshot(
            id,
            name,
            fanSensor?.Value,
            currentControlPercent,
            control is not null,
            deviceKind,
            capabilityMessage,
            hardwareSource,
            sensorIndex);
    }

    private static ISensor? FindBestControlSensor(ISensor fanSensor, List<ISensor> controlSensors)
    {
        if (fanSensor.Control is not null)
        {
            return fanSensor;
        }

        string normalizedFanName = NormalizeSensorName(fanSensor.Name);

        return controlSensors.FirstOrDefault(controlSensor =>
            controlSensor.Hardware.Identifier.ToString() == fanSensor.Hardware.Identifier.ToString()
            && (controlSensor.Index == fanSensor.Index
                || NormalizeSensorName(controlSensor.Name) == normalizedFanName
                || NormalizeSensorName(controlSensor.Name).Contains(normalizedFanName, StringComparison.OrdinalIgnoreCase)
                || normalizedFanName.Contains(NormalizeSensorName(controlSensor.Name), StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeSensorName(string name)
    {
        return name
            .Replace("control", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("fan", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("#", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static bool IsCpuTemperature(ISensor sensor)
    {
        return sensor.Hardware.HardwareType == HardwareType.Cpu
            || sensor.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("tdie", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("tctl", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("package", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("core", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCpuFanSnapshot(FanChannelSnapshot snapshot)
    {
        // Pumps and AIO coolers are not fans — they have their own section.
        if (snapshot.DeviceKind != FanDeviceKind.Fan)
        {
            return false;
        }

        return snapshot.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase)
            || snapshot.Name.Contains("processor", StringComparison.OrdinalIgnoreCase)
            || snapshot.Name.Contains("cpu opt", StringComparison.OrdinalIgnoreCase)
            || snapshot.Name.Contains("cpu_fan", StringComparison.OrdinalIgnoreCase)
            || snapshot.Name.Contains("system fan", StringComparison.OrdinalIgnoreCase)
            || snapshot.Name.Contains("sys fan", StringComparison.OrdinalIgnoreCase)
            || snapshot.Name.Contains("chassis", StringComparison.OrdinalIgnoreCase);
    }

    private static FanDeviceKind ClassifyDevice(string name)
    {
        string lower = name.ToLowerInvariant();

        if (lower.Contains("pump") || lower.Contains("w_pump") || lower.Contains("wpump"))
        {
            return FanDeviceKind.Pump;
        }

        if (lower.Contains("aio") || lower.Contains("liquid") || lower.Contains("cooler")
            || lower.Contains("radiator") || lower.Contains("water"))
        {
            return FanDeviceKind.AioCooler;
        }

        return FanDeviceKind.Fan;
    }

    private static bool IsCpuMetricSensor(ISensor sensor, SensorType sensorType)
    {
        return sensor.SensorType == sensorType
            && (sensor.Hardware.HardwareType == HardwareType.Cpu
                || sensor.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("package", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("core", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGpuMetricSensor(ISensor sensor, SensorType sensorType)
    {
        return sensor.SensorType == sensorType
            && (sensor.Hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
                || sensor.Name.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("graphics", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("hotspot", StringComparison.OrdinalIgnoreCase));
    }

    private static List<CpuReadingSnapshot> BuildCpuReadings(List<ISensor> sensors)
    {
        IEnumerable<CpuReadingSnapshot> readings = sensors
            .Where(sensor => sensor.Value.HasValue)
            .Where(sensor =>
                IsCpuMetricSensor(sensor, SensorType.Temperature)
                || IsCpuMetricSensor(sensor, SensorType.Load)
                || IsCpuMetricSensor(sensor, SensorType.Clock)
                || IsCpuMetricSensor(sensor, SensorType.Power)
                || IsCpuMetricSensor(sensor, SensorType.Voltage))
            .Select(sensor => new CpuReadingSnapshot(sensor.Name, FormatSensorValue(sensor), sensor.SensorType.ToString()))
            .OrderBy(reading => reading.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reading => reading.Name, StringComparer.OrdinalIgnoreCase)
            .Take(24);

        return readings.ToList();
    }

    private static List<GpuReadingSnapshot> BuildGpuReadings(List<ISensor> sensors)
    {
        IEnumerable<GpuReadingSnapshot> readings = sensors
            .Where(sensor => sensor.Value.HasValue)
            .Where(sensor =>
                IsGpuMetricSensor(sensor, SensorType.Temperature)
                || IsGpuMetricSensor(sensor, SensorType.Load)
                || IsGpuMetricSensor(sensor, SensorType.Clock)
                || IsGpuMetricSensor(sensor, SensorType.Power)
                || IsGpuMetricSensor(sensor, SensorType.Voltage))
            .Select(sensor => new GpuReadingSnapshot(sensor.Name, FormatSensorValue(sensor), sensor.SensorType.ToString()))
            .OrderBy(reading => reading.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reading => reading.Name, StringComparer.OrdinalIgnoreCase)
            .Take(24);

        return readings.ToList();
    }

    private static string FormatSensorValue(ISensor sensor)
    {
        float value = sensor.Value ?? 0;

        return sensor.SensorType switch
        {
            SensorType.Temperature => $"{value:F1} C",
            SensorType.Load => $"{value:F0}%",
            SensorType.Clock => $"{value:F0} MHz",
            SensorType.Power => $"{value:F1} W",
            SensorType.Voltage => $"{value:F3} V",
            _ => value.ToString("F2", CultureInfo.InvariantCulture)
        };
    }

    private static double? SelectCpuMetric(List<ISensor> sensors, SensorType sensorType, Func<string, bool> preferredName)
    {
        double? preferred = sensors
            .Where(sensor => IsCpuMetricSensor(sensor, sensorType) && preferredName(sensor.Name))
            .Select(sensor => (double?)sensor.Value)
            .FirstOrDefault(value => value.HasValue);

        if (preferred.HasValue)
        {
            return preferred;
        }

        return sensors
            .Where(sensor => IsCpuMetricSensor(sensor, sensorType))
            .Select(sensor => (double?)sensor.Value)
            .FirstOrDefault(value => value.HasValue);
    }

    private static double? AverageCpuMetric(List<ISensor> sensors, SensorType sensorType, Func<string, bool> includedName)
    {
        List<double> values = sensors
            .Where(sensor => IsCpuMetricSensor(sensor, sensorType) && includedName(sensor.Name))
            .Select(sensor => (double?)sensor.Value)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        if (values.Count > 0)
        {
            return values.Average();
        }

        return sensors
            .Where(sensor => IsCpuMetricSensor(sensor, sensorType))
            .Select(sensor => (double?)sensor.Value)
            .FirstOrDefault(value => value.HasValue);
    }

    private static double? SelectGpuMetric(List<ISensor> sensors, SensorType sensorType, Func<string, bool> preferredName)
    {
        double? preferred = sensors
            .Where(sensor => IsGpuMetricSensor(sensor, sensorType) && preferredName(sensor.Name))
            .Select(sensor => (double?)sensor.Value)
            .FirstOrDefault(value => value.HasValue);

        if (preferred.HasValue)
        {
            return preferred;
        }

        return sensors
            .Where(sensor => IsGpuMetricSensor(sensor, sensorType))
            .Select(sensor => (double?)sensor.Value)
            .FirstOrDefault(value => value.HasValue);
    }

    private static void CollectSensors(IHardware hardware, List<ISensor> sensors)
    {
        hardware.Update();
        sensors.AddRange(hardware.Sensors);

        foreach (IHardware child in hardware.SubHardware)
        {
            CollectSensors(child, sensors);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}