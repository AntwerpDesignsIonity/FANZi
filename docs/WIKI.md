# FANZi — Complete Wiki

> **Version:** 1.0.0 · **Author:** Johan Wilhelm van Antwerp · **Company:** Ionity Global (Pty) Ltd

---

## Table of Contents

1. [What is FANZi?](#1-what-is-fanzi)
2. [Technology Stack](#2-technology-stack)
3. [Architecture Overview](#3-architecture-overview)
4. [Models Layer](#4-models-layer)
5. [Services Layer](#5-services-layer)
   - [HardwareMonitorService](#hardwaremonitorservice)
   - [OpenRgbService](#openrgbservice)
   - [RgbEffectsEngine](#rgbeffectsengine)
   - [SettingsService](#settingsservice)
   - [EmailNotificationService](#emailnotificationservice)
6. [ViewModels Layer](#6-viewmodels-layer)
   - [MainWindowViewModel](#mainwindowviewmodel)
   - [FanChannelViewModel](#fanchannelviewmodel)
   - [ProfileTabViewModel](#profiletabviewmodel)
   - [RgbControlViewModel](#rgbcontrolviewmodel)
7. [Views Layer](#7-views-layer)
8. [RGB Lighting System](#8-rgb-lighting-system)
9. [Fan Control System](#9-fan-control-system)
10. [Settings & Persistence](#10-settings--persistence)
11. [Email Notification System](#11-email-notification-system)
12. [Cross-Platform Behaviour](#12-cross-platform-behaviour)
13. [Launchers](#13-launchers)
14. [Dependency Reference](#14-dependency-reference)
15. [Frequently Asked Questions](#15-frequently-asked-questions)

---

## 1. What is FANZi?

**FANZi** is a real-time hardware telemetry and fan-management desktop application for Windows. It was created by **Ionity Global (Pty) Ltd** to give power users and enthusiasts a single, elegant dashboard for:

- Monitoring CPU and GPU temperatures, clocks, power, and load.
- Viewing live RPM readings from every fan channel the motherboard exposes.
- Overriding individual fan channel speeds via software PWM.
- Persisting named fan profiles with per-channel overrides.
- Driving all [OpenRGB](https://openrgb.org)-compatible devices with a 30 fps built-in effects engine (9 effect types, 12 theme presets).
- Sending SMTP-based thermal-alert emails when a temperature threshold is exceeded.

The **UI layer** is built with [Avalonia](https://avaloniaui.net/) and is technically cross-platform. However, the underlying **hardware sensor access** relies on [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), which has full kernel-level driver support only on **Windows**. On macOS/Linux the UI launches normally but telemetry returns a graceful no-data state.

---

## 2. Technology Stack

| Component | Library / Framework | Version |
|---|---|---|
| UI Framework | [Avalonia](https://avaloniaui.net/) | 11.3.12 |
| UI Theme | Avalonia Fluent theme | 11.3.12 |
| Font | Inter (via Avalonia.Fonts.Inter) | 11.3.12 |
| MVVM Toolkit | [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) | 8.2.1 |
| Hardware Sensors | [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | 0.9.6 |
| RGB Control | [OpenRGB.NET](https://github.com/diogotr7/OpenRGB.NET) | 3.1.1 |
| Target Framework | .NET 10.0 | — |
| Language | C# 13 | — |

---

## 3. Architecture Overview

FANZi follows the **MVVM (Model-View-ViewModel)** architectural pattern, reinforced by a clean service layer:

```
┌──────────────────────────────────────────────────────┐
│                    Views  (AXAML)                     │
│  MainWindow.axaml          RgbControlView.axaml       │
└───────────────────────┬──────────────────────────────┘
                        │ compiled bindings (two-way)
┌───────────────────────▼──────────────────────────────┐
│                 ViewModels  (C#)                       │
│  MainWindowViewModel   FanChannelViewModel             │
│  ProfileTabViewModel   RgbControlViewModel             │
└──────┬──────────────────────────┬────────────────────┘
       │ IHardwareMonitorService  │ IRgbService
       │ ISettingsService         │
┌──────▼──────────────────────────▼────────────────────┐
│                  Services  (C#)                        │
│  HardwareMonitorService    OpenRgbService              │
│  SettingsService           EmailNotificationService    │
│  RgbEffectsEngine (static) IHardwareMonitorService    │
└──────────────────────────────────────────────────────┘
       │                          │
       ▼                          ▼
 LibreHardwareMonitor         OpenRGB (external app)
 (kernel driver, Windows)     (SDK server, port 6742)
```

### Key Design Principles

- **Interfaces over implementations** — `IHardwareMonitorService`, `IRgbService`, and `ISettingsService` are injected into ViewModels, keeping the UI testable and the hardware layer swappable.
- **Thread safety** — `HardwareMonitorService` uses a `lock (_syncRoot)` around all LibreHardwareMonitor calls (which are not thread-safe). `RgbControlViewModel` writes to `volatile` fields so the 30 fps timer and the UI thread can share data safely.
- **Graceful degradation** — if LibreHardwareMonitor cannot access sensors (non-Windows, non-admin, etc.) the service returns a snapshot with a descriptive `StatusMessage` and empty collections; the UI displays the message instead of crashing.
- **Async/Await throughout** — service calls are `Task`-based; the UI never blocks the Avalonia dispatcher.

---

## 4. Models Layer

All models are in `src/Fanzi.FanControl/Models/`. They are plain data containers with no business logic.

### `HardwareSnapshot`

A `record` representing a full point-in-time hardware reading:

| Property | Type | Description |
|---|---|---|
| `Timestamp` | `DateTimeOffset` | UTC time of the snapshot |
| `CpuPackageTemperature` | `double?` | CPU die package temperature (°C) |
| `CpuAverageTemperature` | `double?` | Average across all CPU cores (°C) |
| `CpuHotspotTemperature` | `double?` | Hottest individual core (°C) |
| `CpuTotalLoadPercent` | `double?` | Total CPU utilisation (%) |
| `CpuAverageClockMhz` | `double?` | Average clock across all cores (MHz) |
| `CpuPackagePowerWatts` | `double?` | CPU TDP power draw (W) |
| `CpuCoreVoltage` | `double?` | Core VID voltage (V) |
| `GpuCoreTemperature` | `double?` | GPU die temperature (°C) |
| `GpuHotspotTemperature` | `double?` | GPU hotspot (°C) |
| `GpuLoadPercent` | `double?` | GPU utilisation (%) |
| `GpuCoreClockMhz` | `double?` | GPU core clock (MHz) |
| `GpuPowerWatts` | `double?` | GPU power draw (W) |
| `CpuFan` | `FanChannelSnapshot?` | Dedicated CPU fan reading |
| `CpuReadings` | `IReadOnlyList<CpuReadingSnapshot>` | Per-core clock and load readings |
| `GpuReadings` | `IReadOnlyList<GpuReadingSnapshot>` | Per-GPU sensor readings |
| `Fans` | `IReadOnlyList<FanChannelSnapshot>` | All detected fan channels |
| `StatusMessage` | `string` | Human-readable status or error string |
| `CpuName` | `string?` | Detected CPU model name |
| `GpuName` | `string?` | Detected GPU model name |
| `GpuMemoryUsedMb` | `double?` | VRAM used (MB) |
| `GpuMemoryTotalMb` | `double?` | VRAM total (MB) |

### `FanChannelSnapshot`

One detected fan channel:

| Property | Type | Description |
|---|---|---|
| `ChannelId` | `string` | Unique ID used to address the channel |
| `Name` | `string` | Display name (e.g. "CPU Fan") |
| `CurrentRpm` | `double?` | Live RPM reading |
| `CurrentPercent` | `double?` | Current % if software-controlled |
| `IsControllable` | `bool` | Whether software PWM is supported |

### `FanProfile`

A persisted user profile:

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | Auto-generated GUID |
| `Name` | `string` | Display name |
| `CpuFanDesiredPercent` | `double` | Default fan % for CPU channel |
| `CpuWarningThresholdDegrees` | `double` | Temperature (°C) that triggers an alert |
| `NotificationEmail` | `string` | Recipient for thermal alerts |
| `FanChannelPercents` | `Dictionary<string,double>` | Per-channel speed overrides |

### `AppSettings`

Top-level settings container written to `%APPDATA%\FANZI\settings.json`:

| Property | Type | Description |
|---|---|---|
| `Profiles` | `List<FanProfile>` | All saved profiles |
| `ActiveProfileId` | `string?` | ID of the currently active profile |

### `RgbColor`

An immutable RGB colour value (`byte R, G, B`). Common named colours are provided as static fields (`RgbColor.Red`, `RgbColor.Blue`, `RgbColor.White`, etc.).

### `RgbEffectType`

Enum of all supported lighting effects (see [RGB Lighting System](#8-rgb-lighting-system)).

### `RgbThemePreset`

A `record` bundling an effect type with colour choices, speed, and brightness. Twelve built-in presets are defined as `static readonly` fields and collected in `RgbThemePreset.All`.

### `RgbDeviceInfo`

Describes one OpenRGB device: `Name`, `Type`, `LedCount`.

### `CpuReadingSnapshot` / `GpuReadingSnapshot`

Per-core / per-GPU sensor snapshots with label, clock MHz, load %, and temperature.

### `SetFanControlResult`

Result returned by fan-control operations: `Success` (bool) and `Message` (string).

---

## 5. Services Layer

### HardwareMonitorService

**File:** `src/Fanzi.FanControl/Services/HardwareMonitorService.cs`  
**Interface:** `IHardwareMonitorService`

Wraps [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) to expose CPU, GPU, and fan sensor data.

**Enabled hardware:**
- CPU (temperature, load, clock, power, voltage)
- Motherboard (embedded fan controllers)
- Controllers (add-in fan controllers)
- GPU (temperature, clock, load, power, VRAM)

**Key methods:**

| Method | Description |
|---|---|
| `GetSnapshotAsync()` | Returns a full `HardwareSnapshot`. Thread-safe — locks internally. |
| `SetFanControlAsync(channelId, percent)` | Applies software PWM to the specified fan channel. |
| `RestoreAutomaticControlAsync(channelId)` | Releases software control; motherboard resumes management. |

**Thread safety:** All LibreHardwareMonitor calls are wrapped in `lock (_syncRoot)`.

**Non-Windows behaviour:** The constructor is a no-op on non-Windows platforms. `GetSnapshotAsync` returns an empty snapshot with an explanatory `StatusMessage`.

**Admin rights:** LibreHardwareMonitor sensor access (especially fan PWM) typically requires the process to be elevated on Windows.

---

### OpenRgbService

**File:** `src/Fanzi.FanControl/Services/OpenRgbService.cs`  
**Interface:** `IRgbService`

Connects to a running [OpenRGB](https://openrgb.org) application via its SDK server (default: `localhost:6742`).

**Key methods:**

| Method | Description |
|---|---|
| `TryConnectAsync(host, port)` | Attempts connection; returns `false` gracefully if server is unreachable. |
| `GetDevicesAsync()` | Returns `IReadOnlyList<RgbDeviceInfo>` with name, type, LED count. |
| `SetDeviceColorAsync(index, color)` | Sets all LEDs on one device to a single colour. |
| `SetDeviceColorsAsync(index, colors[])` | Sets per-LED colours on one device. |
| `SetAllDevicesColorAsync(color)` | Broadcasts a colour to every device simultaneously. |
| `Disconnect()` | Gracefully closes the SDK connection. |

If OpenRGB is not running, `TryConnectAsync` catches the connection exception and returns `false`; all subsequent colour-set calls are silently no-ops.

---

### RgbEffectsEngine

**File:** `src/Fanzi.FanControl/Services/RgbEffectsEngine.cs`

A **pure, stateless, static** colour-computation class. Given elapsed time (seconds), hardware readings, and user settings, it computes the `RgbColor` that each LED should display at that instant.

`Tick(effect, elapsed, primary, secondary, speed, brightness, cpuTemp, gpuTemp, cpuLoad, deviceIndex, deviceCount)` → `RgbColor`

Calling this at 30 fps (every ~33 ms) produces smooth animations. The ViewModel owns the timer and passes the incrementing elapsed time.

---

### SettingsService

**File:** `src/Fanzi.FanControl/Services/SettingsService.cs`  
**Interface:** `ISettingsService`

Persists `AppSettings` to `%APPDATA%\FANZI\settings.json` using `System.Text.Json`.

**Atomic writes:** Settings are first written to a `.tmp` file then moved atomically to avoid corruption on crash.

**Error handling:** Serialisation errors are swallowed silently; the app continues with in-memory state.

---

### EmailNotificationService

**File:** `src/Fanzi.FanControl/Services/EmailNotificationService.cs`

Sends a plain-text email via SMTP using `System.Net.Mail.SmtpClient`. SSL is always enabled. Returns a human-readable result string suitable for display in the UI.

---

## 6. ViewModels Layer

### MainWindowViewModel

**File:** `src/Fanzi.FanControl/ViewModels/MainWindowViewModel.cs`

The root ViewModel. Owns the hardware polling loop, fan channel state, profile management, and coordinates all child ViewModels.

**Collections exposed to the View:**

| Property | Type | Description |
|---|---|---|
| `FanChannels` | `ObservableCollection<FanChannelViewModel>` | One entry per detected fan |
| `CpuReadings` | `ObservableCollection<CpuReadingSnapshot>` | Per-core readings |
| `GpuReadings` | `ObservableCollection<GpuReadingSnapshot>` | Per-GPU sensor readings |
| `Profiles` | `ObservableCollection<ProfileTabViewModel>` | Saved profiles |

**Commands:**

| Command | Description |
|---|---|
| `RefreshCommand` | Polls hardware and updates all collections |
| `ApplyCpuFanCommand` | Applies the active profile's CPU fan % |
| `AutoCpuFanCommand` | Restores automatic control for the CPU fan |
| `ToggleHelpCommand` | Shows/hides the contextual help overlay |
| `AddProfileCommand` | Creates and activates a new profile |
| `SendTestEmailCommand` | Sends a test alert email |

**Startup:** `RunStartupAsync()` is called from the constructor; it loads settings, initialises profiles, and fires the first hardware poll.

---

### FanChannelViewModel

**File:** `src/Fanzi.FanControl/ViewModels/FanChannelViewModel.cs`

Wraps a single `FanChannelSnapshot` and exposes slider-bindable `DesiredPercent` plus Apply / Auto commands.

---

### ProfileTabViewModel

**File:** `src/Fanzi.FanControl/ViewModels/ProfileTabViewModel.cs`

Wraps a `FanProfile` and exposes editable properties bound to the profile editor panel. Calls `ISettingsService.SaveAsync` on every relevant change.

---

### RgbControlViewModel

**File:** `src/Fanzi.FanControl/ViewModels/RgbControlViewModel.cs`

Drives the RGB subsystem:

- Maintains a 30 fps `System.Timers.Timer`.
- On each tick calls `RgbEffectsEngine.Tick(...)` for each device.
- Pushes the computed colour to `IRgbService`.
- Exposes all user-configurable settings (effect type, primary/secondary colour R/G/B sliders, hex colour inputs, speed, brightness, hardware-reactive toggle, theme presets) as `[ObservableProperty]` fields via CommunityToolkit.Mvvm source generators.
- Receives live hardware data from `MainWindowViewModel` via `UpdateHardwareData(cpuTemp, gpuTemp, cpuLoad)` using `volatile` fields for thread-safe reads.

---

## 7. Views Layer

### MainWindow.axaml

The primary application window. Uses a dark navy colour scheme (`#060E1A` background). Layout:

- **Tab strip** — Dashboard, RGB Control (and potentially more in future)
- **Dashboard tab:**
  - CPU card (temperature metrics, load, clock, power, voltage)
  - GPU card (temperature, load, clock, power, VRAM)
  - Fan Channels panel (scrollable list of `FanChannelViewModel` rows)
  - Profiles panel (horizontal tab strip + editor form)
- **RGB tab:** embedded `RgbControlView`

All bindings use **compiled bindings** (`x:DataType`) for performance and compile-time safety.

### RgbControlView.axaml

A dedicated view for RGB control. Contains:

- Effect type `ComboBox`
- Theme preset `ListBox`
- Primary and secondary colour pickers (R/G/B sliders + hex text box)
- Speed and brightness sliders
- Hardware-reactive toggle
- Device connection status display

---

## 8. RGB Lighting System

The RGB pipeline consists of three layers:

```
RgbControlViewModel (timer, settings, hardware data)
    │
    ▼  RgbEffectsEngine.Tick(...)
    │
    ▼  RgbColor per device
    │
    ▼  IRgbService.SetDeviceColorAsync(...)
    │
    ▼  OpenRGB SDK server
    │
    ▼  Physical RGB hardware
```

### Effect Computation (`RgbEffectsEngine`)

All effects are computed from `elapsed` (seconds since start) so they are frame-rate independent. A 30 fps timer in `RgbControlViewModel` drives the loop.

| Effect | Algorithm |
|---|---|
| Static | Returns `primary` unchanged |
| Pulse | `brightness × sin²(π × elapsed × speed)` applied to primary colour |
| Rainbow | HSV hue = `(elapsed × speed × 60) mod 360`, full saturation |
| ColorWave | Per-device phase offset creates a rolling colour wave |
| TemperatureReactive | Linear interpolation: `IceBlue` (≤40°C) → `Red` (≥90°C) |
| CpuLoadReactive | Speed and brightness scale with CPU load % |
| Performance | Combines TemperatureReactive colour with CpuLoadReactive speed |
| Strobe | Binary on/off at `speed × 8` Hz |
| DualColorFlash | Alternates `primary`/`secondary` at `speed × 4` Hz |

### Thread Safety in RgbControlViewModel

Hardware temperature values (`_cpuTempC`, `_gpuTempC`, `_cpuLoadPct`) are declared `volatile`. The UI thread writes them via `UpdateHardwareData()`; the 30 fps timer thread reads them in `SendFrameToHardwareAsync()`.

---

## 9. Fan Control System

```
User moves slider / clicks Apply
    │
    ▼  FanChannelViewModel.ApplyCommand
    │
    ▼  IHardwareMonitorService.SetFanControlAsync(channelId, percent)
    │
    ▼  HardwareMonitorService (lock) → IControl.SetSoftware(value)
    │
    ▼  LibreHardwareMonitor → motherboard EC / embedded controller
    │
    ▼  Physical fan speed changes
```

**Restoring auto control:**
```
FanChannelViewModel.AutoCommand
    → IHardwareMonitorService.RestoreAutomaticControlAsync(channelId)
    → IControl.SetDefault()
```

Fan channels are discovered during `GetSnapshotAsync()`. Any sensor of type `Fan` is paired with the matching `Control` sensor (same hardware parent). Channels that have no associated `Control` are marked `IsControllable = false` and the slider is disabled in the UI.

---

## 10. Settings & Persistence

Settings are stored at:

```
%APPDATA%\FANZI\settings.json
```

Example structure:

```json
{
  "Profiles": [
    {
      "Id": "abc123",
      "Name": "Silent",
      "CpuFanDesiredPercent": 40,
      "CpuWarningThresholdDegrees": 85,
      "NotificationEmail": "admin@example.com",
      "FanChannelPercents": {
        "cpu_fan_0": 40,
        "case_fan_1": 35
      }
    }
  ],
  "ActiveProfileId": "abc123"
}
```

Loading and saving use `System.Text.Json` with `WriteIndented = true` and `PropertyNameCaseInsensitive = true` for robust round-tripping.

---

## 11. Email Notification System

When a profile has a `NotificationEmail` set and the CPU temperature exceeds `CpuWarningThresholdDegrees`, `MainWindowViewModel` calls `EmailNotificationService.SendAsync(...)`.

SMTP credentials are entered in the Profile editor panel. The email is sent over SSL (port 587 recommended for Gmail/Outlook).

**Security note:** SMTP passwords are stored only in memory during the session and written to the profile's settings JSON. Use an app-specific password (not your main account password) for safety.

---

## 12. Cross-Platform Behaviour

| Feature | Windows | macOS | Linux |
|---|---|---|---|
| UI rendering | ✅ Full | ✅ Full | ✅ Full |
| Hardware telemetry | ✅ Full (admin recommended) | ❌ Not available | ❌ Not available |
| Fan speed control | ✅ Full (admin required) | ❌ Not available | ❌ Not available |
| RGB control | ✅ (OpenRGB server) | ✅ (OpenRGB server) | ✅ (OpenRGB server) |
| Settings persistence | ✅ `%APPDATA%\FANZI\` | ✅ `~/Library/Application Support/FANZI/` | ✅ `~/.config/FANZI/` |

On non-Windows the status bar shows:  
*"Hardware access is only enabled on Windows. The UI still runs cross-platform, but fan telemetry requires Windows sensor support."*

---

## 13. Launchers

The `launchers/` directory contains convenience scripts:

| File | Platform | Behaviour |
|---|---|---|
| `FANZI-Windows.bat` | Windows | `dotnet run ... --configuration Release` + `pause` |
| `FANZI-Mac.command` | macOS | Same, but resolves the script directory first |
| `FANZI-Linux.sh` | Linux | Same as Mac script |

All launchers change directory to the repository root before invoking `dotnet run`, so relative paths resolve correctly.

---

## 14. Dependency Reference

| Package | Purpose | Version |
|---|---|---|
| `Avalonia` | Cross-platform XAML UI framework | 11.3.12 |
| `Avalonia.Desktop` | Desktop lifetime support | 11.3.12 |
| `Avalonia.Themes.Fluent` | Fluent design theme | 11.3.12 |
| `Avalonia.Fonts.Inter` | Inter font embedding | 11.3.12 |
| `Avalonia.Diagnostics` | Dev-time inspector (excluded from Release) | 11.3.12 |
| `CommunityToolkit.Mvvm` | Source-generated MVVM boilerplate | 8.2.1 |
| `LibreHardwareMonitorLib` | Windows kernel-level hardware sensors | 0.9.6 |
| `OpenRGB.NET` | OpenRGB SDK client | 3.1.1 |

---

## 15. Frequently Asked Questions

**Q: Fan sliders do nothing — why?**  
A: LibreHardwareMonitor needs elevated privileges for PWM control. Run FANZi as **Administrator**.

**Q: No hardware data is shown.**  
A: Either you are not on Windows, or the app is not running as Administrator. Check the status bar message.

**Q: OpenRGB shows "Not connected".**  
A: Make sure the OpenRGB application is running and its SDK Server is started (Settings → SDK Server → Start Server). The default port is 6742.

**Q: Where are my settings saved?**  
A: `%APPDATA%\FANZI\settings.json` on Windows.

**Q: Can I add my own RGB effect?**  
A: Yes. Add a new value to `RgbEffectType`, add a corresponding `case` in `RgbEffectsEngine.Tick`, and add it to the ComboBox in `RgbControlView.axaml`.

**Q: Can I use FANZi on macOS or Linux?**  
A: The UI launches on any Avalonia-supported platform, but hardware telemetry and fan control require Windows. RGB control via OpenRGB works on any platform.

**Q: My settings were lost after an update.**  
A: The JSON schema is designed to be forward-compatible with `PropertyNameCaseInsensitive`. If deserialization fails, the app silently falls back to defaults. If you see this, check `%APPDATA%\FANZI\settings.json` for corruption.
