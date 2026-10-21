using Fanzi.FanControl.Models;
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>The parts of a sensor used to pair fan readings with their control channel.</summary>
internal readonly record struct SensorMatchKey(string HardwareId, int Index, string Name);

public sealed class HardwareMonitorService : IHardwareMonitorService
{
    private readonly object _syncRoot = new();
    private readonly Computer? _computer;
    private readonly Dictionary<string, IControl> _controlMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private volatile bool _biosFanControl;

    /// <inheritdoc />
    /// <remarks>
    /// Hand-back uses LibreHardwareMonitor's <see cref="IControl.SetDefault"/>, which restores the header registers the
    /// Super IO chip held when FANZi started — i.e. the mode and curve the BIOS programmed. It is applied on the next
    /// snapshot (≤ 1 poll) and re-applied every poll, so nothing in FANZi (AI Auto-Fan, profiles, curves) can pin a
    /// motherboard header while the option is on.
    /// </remarks>
    public bool BiosFanControl
    {
        get => _biosFanControl;
        set
        {
            if (_biosFanControl == value) return;
            _biosFanControl = value;
            AppLog.Info(value
                ? "Fans: BIOS fan control on — motherboard headers follow the BIOS Smart Fan settings."
                : "Fans: BIOS fan control off — FANZi may drive motherboard headers.");
        }
    }

    /// <summary>Motherboard fan headers: Super IO chip, embedded controller, or anything hanging off the board.</summary>
    internal static bool IsBoardHeader(HardwareType type, HardwareType? parentType) =>
        type is HardwareType.SuperIO or HardwareType.Motherboard or HardwareType.EmbeddedController
        || parentType is HardwareType.Motherboard;

    private static bool IsBoardHeader(IHardware? hardware) =>
        hardware is not null && IsBoardHeader(hardware.HardwareType, hardware.Parent?.HardwareType);

    public HardwareMonitorService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var computer = new Computer
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

        try
        {
            computer.Open();
            _computer = computer;
            AppLog.Info("Hardware monitor opened (LibreHardwareMonitor).");
        }
        catch (Exception ex)
        {
            // A broken/blocked ring-0 driver must not take the whole app down.
            InitializationError = $"Hardware monitor failed to start ({ex.GetType().Name}: {ex.Message}). " +
                                  "Run FANZi as Administrator and check that no other tool is blocking the sensor driver.";
            AppLog.Error("Hardware monitor failed to open", ex);
            try { computer.Close(); } catch { }
        }
    }

    /// <summary>Set when LibreHardwareMonitor could not be opened; null when sensors are available.</summary>
    public string? InitializationError { get; }

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

        // Sensor polling (hardware.Update) can take hundreds of ms — keep it off the UI thread.
        return Task.Run(() => GetSnapshotCore(cancellationToken), cancellationToken);
    }

    private HardwareSnapshot GetSnapshotCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows() || _computer is null)
        {
            return new HardwareSnapshot(
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
                InitializationError
                    ?? "Hardware access is only enabled on Windows. The UI still runs cross-platform, but fan telemetry requires Windows sensor support.",
                null,
                null,
                null,
                null);
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

            // Fallback 1: prefer ANY pump (water-cooled system has no CPU fan, just a pump).
            if (cpuFan is null)
            {
                cpuFan = fanSnapshots.FirstOrDefault(f => f.DeviceKind == FanDeviceKind.Pump);
            }

            // Fallback 2: prefer any AIO/liquid cooler channel.
            if (cpuFan is null)
            {
                cpuFan = fanSnapshots.FirstOrDefault(f => f.DeviceKind == FanDeviceKind.AioCooler);
            }

            // Fallback 3: pick the fastest-spinning fan (CPU coolers tend to spin highest under load).
            if (cpuFan is null && fanSnapshots.Count > 1)
            {
                cpuFan = fanSnapshots
                    .Where(f => f.SpeedRpm.HasValue && f.SpeedRpm.Value > 300)
                    .OrderByDescending(f => f.SpeedRpm ?? 0)
                    .FirstOrDefault();
            }

            // Fallback 4: if exactly one channel exists, use it.
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
                0 when cpuTemperatures.Count == 0 && !IsRunningAsAdministrator() =>
                    "No sensors detected. The application must run as Administrator so LibreHardwareMonitor can access the ring-0 kernel driver.",
                0 when cpuTemperatures.Count == 0 =>
                    "No CPU temperature or fan sensors were exposed. Try running elevated or confirm that your motherboard is supported by LibreHardwareMonitor.",
                0 => "CPU temperatures are available, but no fan channels were exposed by the motherboard or attached controller.",
                _ when cpuFan is null => "Fan telemetry is live, but a dedicated CPU fan header was not clearly identified. Generic fan channels are still shown below.",
                _ when fanSnapshots.All(fan => !fan.CanControl) => "Fan telemetry is active. Manual fan control is disabled because this hardware did not expose software-controllable channels.",
                _ => "Telemetry is live. Apply sends a manual duty target; Auto restores the device default or BIOS-controlled mode for supported channels."
            };

            return new HardwareSnapshot(
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
                gpuMemoryTotalMb);
        }
    }

    public Task<SetFanControlResult> SetFanControlAsync(string channelId, double percent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                if (!_controlMap.TryGetValue(channelId, out IControl? control))
                {
                    return new SetFanControlResult(false, "That fan channel is not currently controllable.");
                }

                float clamped = (float)Math.Clamp(percent, control.MinSoftwareValue, control.MaxSoftwareValue);
                control.SetSoftware(clamped);
                control.Sensor?.Hardware.Update();

                return new SetFanControlResult(true, $"Manual control applied at {clamped.ToString("F0", CultureInfo.InvariantCulture)}%.");
            }
        }, cancellationToken);
    }

    public Task<SetFanControlResult> RestoreAutomaticControlAsync(string channelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                if (!_controlMap.TryGetValue(channelId, out IControl? control))
                {
                    return new SetFanControlResult(false, "That fan channel does not expose a default control mode.");
                }

                control.SetDefault();
                control.Sensor?.Hardware.Update();

                return new SetFanControlResult(true, "Automatic or BIOS control restored for this channel.");
            }
        }, cancellationToken);
    }

    private List<FanChannelSnapshot> BuildFanSnapshots(List<ISensor> sensors)
    {
        _controlMap.Clear();

        List<ISensor> fanSensors = sensors.Where(sensor => sensor.SensorType == SensorType.Fan).ToList();
        List<ISensor> controlSensors = sensors.Where(sensor => sensor.Control is not null).ToList();
        List<SensorMatchKey> controlKeys = controlSensors.Select(ToMatchKey).ToList();
        List<FanChannelSnapshot> fanSnapshots = new();
        HashSet<int> claimedControls = new();
        HashSet<string> usedIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (ISensor fanSensor in fanSensors)
        {
            ISensor? controlSensor = null;
            if (fanSensor.Control is not null)
            {
                // The fan sensor carries its own control — claim it so no other fan grabs it.
                controlSensor = fanSensor;
                int own = controlSensors.FindIndex(sensor => ReferenceEquals(sensor, fanSensor));
                if (own >= 0)
                {
                    claimedControls.Add(own);
                }
            }
            else
            {
                int match = FindBestControlIndex(ToMatchKey(fanSensor), controlKeys, claimedControls);
                if (match >= 0)
                {
                    claimedControls.Add(match);
                    controlSensor = controlSensors[match];
                }
            }

            fanSnapshots.Add(CreateFanSnapshot(fanSensor, controlSensor, usedIds));
        }

        for (int i = 0; i < controlSensors.Count; i++)
        {
            if (claimedControls.Contains(i))
            {
                continue;
            }

            fanSnapshots.Add(CreateFanSnapshot(null, controlSensors[i], usedIds));
        }

        return fanSnapshots
            .OrderBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static SensorMatchKey ToMatchKey(ISensor sensor) =>
        new(sensor.Hardware.Identifier.ToString(), sensor.Index, sensor.Name);

    private FanChannelSnapshot CreateFanSnapshot(ISensor? fanSensor, ISensor? controlSensor, ISet<string> usedIds)
    {
        IControl? control = controlSensor?.Control;
        string id = PickUniqueChannelId(
            control?.Identifier.ToString(),
            fanSensor?.Identifier.ToString(),
            usedIds);

        // 4.4: BIOS fan control — board headers go back to the BIOS and are not offered for manual duty.
        bool biosOwned = control is not null && _biosFanControl
            && IsBoardHeader(controlSensor?.Hardware ?? fanSensor?.Hardware);
        if (biosOwned && control!.ControlMode == ControlMode.Software)
        {
            try
            {
                control.SetDefault();
                AppLog.Info($"Fans: '{controlSensor?.Name ?? fanSensor?.Name}' handed back to BIOS control.");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Fans: could not hand '{controlSensor?.Name ?? fanSensor?.Name}' back to the BIOS", ex);
            }
        }

        if (control is not null && !biosOwned)
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
        if (biosOwned)
        {
            // BIOS-driven: the control register value is the BIOS's own duty; show it only as telemetry.
            currentControlPercent = controlSensor?.Value is float biosValue ? biosValue : null;
        }
        else if (controlSensor?.Value is float controlValue)
        {
            currentControlPercent = controlValue;
        }
        else if (control is not null && control.ControlMode == ControlMode.Software)
        {
            currentControlPercent = control.SoftwareValue;
        }

        FanDeviceKind deviceKind = ClassifyDevice(name);

        string capabilityMessage = biosOwned
            ? "BIOS fan control — this header follows your BIOS Smart Fan settings (FANZi only reads it). Turn off 'Use BIOS fan settings' to drive it from FANZi."
            : control switch
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
            control is not null && !biosOwned,
            deviceKind,
            capabilityMessage,
            hardwareSource,
            sensorIndex);
    }

    /// <summary>
    /// Picks the control channel that belongs to a fan sensor. A control matches only when it is on the
    /// same hardware AND either its normalized name is exactly equal (non-empty names only) or it has the
    /// same sensor index. Exact name matches win over index matches. Already-claimed controls are skipped,
    /// so two fans never share one control. Returns the index into <paramref name="controls"/>, or -1.
    /// </summary>
    internal static int FindBestControlIndex(
        SensorMatchKey fan,
        IReadOnlyList<SensorMatchKey> controls,
        IReadOnlySet<int>? claimed = null)
    {
        string fanName = NormalizeSensorName(fan.Name);
        int indexMatch = -1;

        for (int i = 0; i < controls.Count; i++)
        {
            if (claimed?.Contains(i) == true)
            {
                continue;
            }

            SensorMatchKey control = controls[i];
            if (!string.Equals(control.HardwareId, fan.HardwareId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fanName.Length > 0 && string.Equals(NormalizeSensorName(control.Name), fanName, StringComparison.Ordinal))
            {
                return i;
            }

            if (indexMatch < 0 && control.Index == fan.Index)
            {
                indexMatch = i;
            }
        }

        return indexMatch;
    }

    /// <summary>"Fan Control #1" → "1", "CPU Fan" → "cpu", "Fan" → "" (empty never matches by name).</summary>
    internal static string NormalizeSensorName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string stripped = name
            .Replace("control", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("fan", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("#", string.Empty, StringComparison.Ordinal);

        return new string(stripped.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    }

    /// <summary>
    /// Returns a channel ID that is unique within one snapshot: the control identifier, else the fan
    /// sensor identifier, else a deterministic "#n" suffix (stable across polls, unlike a GUID).
    /// </summary>
    internal static string PickUniqueChannelId(string? preferredId, string? fallbackId, ISet<string> usedIds)
    {
        if (!string.IsNullOrEmpty(preferredId) && usedIds.Add(preferredId))
        {
            return preferredId;
        }

        if (!string.IsNullOrEmpty(fallbackId) && usedIds.Add(fallbackId))
        {
            return fallbackId;
        }

        string baseId = !string.IsNullOrEmpty(preferredId) ? preferredId
            : !string.IsNullOrEmpty(fallbackId) ? fallbackId
            : "channel";

        if (usedIds.Add(baseId))
        {
            return baseId;
        }

        for (int n = 2; ; n++)
        {
            string candidate = $"{baseId}#{n.ToString(CultureInfo.InvariantCulture)}";
            if (usedIds.Add(candidate))
            {
                return candidate;
            }
        }
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
        // Pumps, AIO coolers, watercoolers, and IO pumps ARE valid CPU cooling devices
        if (snapshot.DeviceKind is FanDeviceKind.Pump or FanDeviceKind.AioCooler
            or FanDeviceKind.Watercooler or FanDeviceKind.IoPump)
        {
            return snapshot.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("aio", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("w_pump", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("cooler", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("liquid", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("water", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("io", StringComparison.OrdinalIgnoreCase)
                // Known AIO / watercooler vendors
                || snapshot.Name.Contains("asetek", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("kraken", StringComparison.OrdinalIgnoreCase)   // NZXT
                || snapshot.Name.Contains("h100", StringComparison.OrdinalIgnoreCase)     // Corsair Hydro
                || snapshot.Name.Contains("h115", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("h150", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("ryujin", StringComparison.OrdinalIgnoreCase)   // ASUS ROG Ryujin
                || snapshot.Name.Contains("eisbaer", StringComparison.OrdinalIgnoreCase)  // Alphacool
                || snapshot.Name.Contains("au pump", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("a_pump", StringComparison.OrdinalIgnoreCase)
                // EKWB / custom loop
                || snapshot.Name.Contains("ekwb", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("ek-", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("barrow", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("bykski", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("swiftech", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("alphacool", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("optimum", StringComparison.OrdinalIgnoreCase)
                // IO pump signatures
                || snapshot.Name.Contains("io_pump", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("io pump", StringComparison.OrdinalIgnoreCase)
                || snapshot.Name.Contains("ionity", StringComparison.OrdinalIgnoreCase);
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

        // IO Pump signatures (Ionity / custom IO cooling)
        if (lower.Contains("io_pump") || lower.Contains("io pump") || lower.Contains("io-pump")
            || lower.Contains("ionity") || lower.Contains("io cooler"))
        {
            return FanDeviceKind.IoPump;
        }

        // Watercooler / custom loop signatures
        if (lower.Contains("watercool") || lower.Contains("water cool") || lower.Contains("custom loop")
            || lower.Contains("ekwb") || lower.Contains("ek-") || lower.Contains("barrow")
            || lower.Contains("bykski") || lower.Contains("swiftech") || lower.Contains("alphacool")
            || lower.Contains("optimum") || lower.Contains("reservoir") || lower.Contains("res")
            || lower.Contains("coolant") || lower.Contains("block") || lower.Contains("rad")
            || lower.Contains("radiator") || lower.Contains("water block"))
        {
            return FanDeviceKind.Watercooler;
        }

        // Pump signatures — include common AIO header names and vendor markers
        if (lower.Contains("pump") || lower.Contains("w_pump") || lower.Contains("wpump")
            || lower.Contains("au pump") || lower.Contains("a_pump") || lower.Contains("apump")
            || lower.Contains("d5") || lower.Contains("ddc")
            || lower.Contains("w_flow") || lower.Contains("flow sensor") || lower.Contains("flow meter"))
        {
            return FanDeviceKind.Pump;
        }

        // AIO / water cooler signatures - cover the common vendors that ship with sensor names
        if (lower.Contains("aio") || lower.Contains("liquid") || lower.Contains("cooler")
            || lower.Contains("water") || lower.Contains("radiator")
            || lower.Contains("asetek") || lower.Contains("kraken")
            || lower.Contains("hydro")  || lower.Contains("ryujin")
            || lower.Contains("eisbaer") || lower.Contains("celsius")
            || lower.Contains("h100")   || lower.Contains("h115") || lower.Contains("h150")
            || lower.Contains("platinum") || lower.Contains("tuf") || lower.Contains("strix"))
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

    /// <summary>Cap on CPU/GPU readings per poll (a 24-thread CPU exposes ~110 temperature/load/clock/power sensors).</summary>
    internal const int MaxSensorReadings = 160;

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
            // Room for every per-core temperature/load on many-core CPUs (the Sensors heatmap reads these);
            // 24 cut the temperatures off because Clock and Load sort first.
            .Take(MaxSensorReadings);

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
            .Take(MaxSensorReadings);

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

    private static bool IsRunningAsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}