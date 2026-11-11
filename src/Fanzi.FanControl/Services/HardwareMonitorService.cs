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
    private volatile bool _pumpProtection = true;
    private double _pumpMaxPercent = PumpProtection.DefaultMaxPercent;
    private readonly PumpSignatureTracker _pumpSignature = new();
    private IReadOnlyDictionary<string, CoolingRole> _channelRoles = new Dictionary<string, CoolingRole>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Channel ids currently classified as pumps (rebuilt every snapshot); duty writes are clamped or refused.</summary>
    private readonly HashSet<string> _pumpChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pumpLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool PumpProtectionEnabled
    {
        get => _pumpProtection;
        set
        {
            if (_pumpProtection == value) return;
            _pumpProtection = value;
            AppLog.Info(value
                ? "Fans: pump protection on — pumps stay on their BIOS / firmware speed."
                : $"Fans: pump protection off by the user — fixed pump duty allowed between {PumpProtection.MinPercent:F0}% and {_pumpMaxPercent:F0}%.");
        }
    }

    /// <inheritdoc />
    public double PumpMaxPercent
    {
        get => _pumpMaxPercent;
        set => _pumpMaxPercent = Math.Clamp(value, PumpProtection.MinPercent, 100);
    }

    /// <inheritdoc />
    public void SetChannelRoles(IReadOnlyDictionary<string, CoolingRole> roles)
    {
        _channelRoles = new Dictionary<string, CoolingRole>(roles, StringComparer.OrdinalIgnoreCase);
    }

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
            // 4.5: the "CPU cooling" channel shown on the dashboard. A real CPU fan header wins; a pump is shown
            // (read-only) only when there is no CPU fan. The old "fastest-spinning channel" fallback is gone — on
            // liquid-cooled PCs the fastest channel is the pump, and AI Auto-Fan then drove the pump with the fan curve.
            FanChannelSnapshot? cpuFan = SelectCpuCooling(fanSnapshots);

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
                    return new SetFanControlResult(false, _pumpChannels.Contains(channelId)
                        ? PumpProtection.ProtectedMessage
                        : "That fan channel is not currently controllable.");
                }

                // 4.5: a pump is never driven below its safe floor or above the user's cap, whoever asks
                // (manual Apply, AI Auto-Fan, a profile, the bench). Protected pumps are not in the control map at all.
                bool isPump = _pumpChannels.Contains(channelId);
                double requested = isPump ? PumpProtection.Clamp(percent, _pumpMaxPercent) : percent;

                float clamped = (float)Math.Clamp(requested, control.MinSoftwareValue, control.MaxSoftwareValue);
                control.SetSoftware(clamped);
                control.Sensor?.Hardware.Update();

                return new SetFanControlResult(true, isPump
                    ? $"Pump set to {clamped.ToString("F0", CultureInfo.InvariantCulture)}% (kept between {PumpProtection.MinPercent:F0}% and {_pumpMaxPercent:F0}%)."
                    : $"Manual control applied at {clamped.ToString("F0", CultureInfo.InvariantCulture)}%.");
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TryRecoverAsync()
    {
        return Task.Run(() =>
        {
            lock (_syncRoot)
            {
                if (_disposed || _computer is null) return false;
                foreach (var control in _controlMap.Values)
                {
                    try { control.SetDefault(); } catch { }
                }
                _controlMap.Clear();
                try
                {
                    _computer.Close();
                    _computer.Open();
                    AppLog.Info("Hardware monitor reopened by Self-Doctor.");
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Error("Hardware monitor could not be reopened", ex);
                    return false;
                }
            }
        });
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
        _pumpChannels.Clear();

        // 4.5: RPM signature — a channel that runs fast and steady next to slower neighbours is a pump even when
        // the board only calls it "Fan #2". Keyed by the fan sensor id; grouped per hardware.
        _pumpSignature.Record(fanSensors.Select(f => (
            Id: f.Identifier.ToString(),
            Group: f.Hardware.Identifier.ToString(),
            Rpm: (double?)f.Value)));

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

        IHardware? owner = fanSensor?.Hardware ?? controlSensor?.Hardware;
        string rawName = fanSensor?.Name
            ?? controlSensor?.Name.Replace("Control", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()
            ?? "Unnamed channel";
        _channelRoles.TryGetValue(id, out CoolingRole userRole);
        var classification = CoolingChannelClassifier.Classify(
            rawName, owner?.HardwareType.ToString(), owner?.Name, userRole,
            rpmSaysPump: fanSensor is not null && _pumpSignature.IsLikelyPump(fanSensor.Identifier.ToString()));

        // 4.5 pump protection: a pump is handed back to its BIOS / firmware setting and is not offered for duty.
        bool pumpProtected = classification.IsPump && _pumpProtection;
        if (classification.IsPump)
        {
            _pumpChannels.Add(id);
            if (_pumpLogged.Add(id))
                AppLog.Info($"Fans: '{rawName}' on {owner?.Name ?? "unknown hardware"} is a pump ({classification.Evidence}) — " +
                            (pumpProtected ? "protected, left on its BIOS / firmware speed." : "unlocked by the user."));
        }
        if (pumpProtected && control is not null && control.ControlMode == ControlMode.Software)
        {
            try
            {
                control.SetDefault();
                AppLog.Info($"Fans: pump '{rawName}' was under software control — handed back to its BIOS / firmware speed.");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Fans: could not hand pump '{rawName}' back", ex);
            }
        }

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

        if (control is not null && !biosOwned && !pumpProtected)
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

        FanDeviceKind deviceKind = classification.Kind;

        string capabilityMessage = pumpProtected
            ? PumpProtection.ProtectedMessage + " " + classification.Reason
            : classification.IsPump && control is not null && !biosOwned
            ? PumpProtection.UnlockedMessage(_pumpMaxPercent)
            : biosOwned
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
            control is not null && !biosOwned && !pumpProtected,
            deviceKind,
            capabilityMessage,
            hardwareSource,
            sensorIndex)
        {
            Role = classification.Role,
            Evidence = classification.Evidence,
            LockReason = pumpProtected ? FanLockReason.PumpProtected
                : biosOwned ? FanLockReason.BiosControl
                : control is null ? FanLockReason.ReadOnlyHardware
                : FanLockReason.None,
        };
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

    /// <summary>
    /// CPU cooling channel for the dashboard: a CPU-fan header first, else a pump (display only), else the only channel.
    /// </summary>
    internal static FanChannelSnapshot? SelectCpuCooling(IReadOnlyList<FanChannelSnapshot> fans)
    {
        return fans.FirstOrDefault(f => f.Role == CoolingRole.CpuFan && f.SpeedRpm is > 0)
            ?? fans.FirstOrDefault(f => f.Role == CoolingRole.CpuFan)
            ?? fans.FirstOrDefault(f => f.IsPump && f.SpeedRpm is > 0)
            ?? fans.FirstOrDefault(f => f.IsPump)
            // Anonymous headers ("Fan #1…#7"): the lowest-numbered spinning board fan is the usual CPU_FAN header.
            ?? fans.Where(f => !f.IsPump && f.Role is CoolingRole.Auto or CoolingRole.SystemFan && f.SpeedRpm is > 0)
                   .OrderBy(f => f.Role == CoolingRole.SystemFan ? 1 : 0).ThenBy(f => f.SensorIndex < 0 ? int.MaxValue : f.SensorIndex)
                   .FirstOrDefault(f => !f.IsPump && f.SpeedRpm is > 0)
            ?? (fans.Count == 1 ? fans[0] : null);
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