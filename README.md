# FANZi — Fan Telemetry & Control

<p align="center">
  <img src="src/Fanzi.FanControl/Assets/ionity-logo.png" alt="FANZi Logo" width="160"/>
</p>

<p align="center">
  <strong>Professional fan-speed telemetry, CPU/GPU temperature monitoring, and RGB lighting control</strong><br/>
  Built with .NET 10 + Avalonia UI · Developed by <a href="https://ionity.global">Ionity Global (Pty) Ltd</a>
</p>

<p align="center">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows-blue?logo=windows"/>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet"/>
  <img alt="License" src="https://img.shields.io/badge/license-Proprietary-red"/>
  <img alt="Version" src="https://img.shields.io/badge/version-1.0.0-green"/>
</p>

---

## Table of Contents

- [Overview](#overview)
- [Features](#features)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
- [Installation](#installation)
- [Usage](#usage)
- [RGB Lighting Effects](#rgb-lighting-effects)
- [Fan Profiles](#fan-profiles)
- [Email Notifications](#email-notifications)
- [Project Structure](#project-structure)
- [Architecture](#architecture)
- [Contributing](#contributing)
- [License](#license)

---

## Overview

**FANZi** (Fan + Zi, styled stylistically) is a desktop application for real-time hardware telemetry and fan management. It reads CPU and GPU sensor data via [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), lets you set individual fan channel speeds, and drives RGB lighting across all [OpenRGB](https://openrgb.org)-compatible devices with a rich built-in effects engine.

The UI is built with [Avalonia](https://avaloniaui.net/) so the rendering layer is cross-platform; however, **hardware sensor access (fan control, temperature readings) requires Windows**, where LibreHardwareMonitor has full kernel-level support.

---

## Features

| Category | Details |
|---|---|
| 🌡️ **Temperature Monitoring** | CPU package, average, and hotspot temperatures; GPU core & hotspot temps |
| ⚡ **Load & Clock Tracking** | CPU total load %, per-core readings, average clock MHz, package power (W) |
| 🎮 **GPU Telemetry** | GPU load %, core clock MHz, power draw, VRAM usage |
| 🌀 **Fan Telemetry** | Per-channel RPM display for all fans detected by LibreHardwareMonitor |
| 🎛️ **Fan Speed Control** | Set any controllable fan channel to a specific % via software PWM |
| 💾 **Fan Profiles** | Save/load named profiles with per-channel percentages and warning thresholds |
| 🌈 **RGB Lighting** | 9 built-in lighting effects, 12 preset themes, per-channel colour pickers |
| 🔔 **Email Alerts** | SMTP-based notifications when CPU temperature exceeds a configurable threshold |
| 🖥️ **Cross-Platform UI** | Avalonia renders on Windows, macOS, and Linux (hardware monitoring on Windows only) |

---

## Requirements

### Runtime
| Requirement | Minimum |
|---|---|
| Operating System | Windows 10 / 11 (64-bit) for full hardware control |
| .NET Runtime | [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) |
| OpenRGB *(optional)* | [OpenRGB](https://openrgb.org) with SDK Server enabled (port 6742) |

### Build
| Tool | Version |
|---|---|
| .NET SDK | 10.0 or later |
| IDE | Visual Studio 2022+, JetBrains Rider, or VS Code with C# extension |

> **Admin rights:** LibreHardwareMonitor requires elevated privileges to read hardware sensors on Windows. Run FANZi as Administrator for full fan-control capability.

---

## Quick Start

```bash
# Clone the repository
git clone https://github.com/AntwerpDesignsIonity/FANZi.git
cd FANZi

# Run directly (Release mode)
dotnet run --project src/Fanzi.FanControl/Fanzi.FanControl.csproj --configuration Release
```

Or use the pre-made launchers in the `launchers/` directory:

| OS | Launcher |
|---|---|
| Windows | `launchers\FANZI-Windows.bat` |
| macOS | `launchers/FANZI-Mac.command` |
| Linux | `launchers/FANZI-Linux.sh` |

---

## Installation

### Option 1 — Run from Source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
2. Clone this repository.
3. Open a terminal in the repository root and run:
   ```bash
   dotnet run --project src/Fanzi.FanControl/Fanzi.FanControl.csproj --configuration Release
   ```

### Option 2 — Build a Self-Contained Executable (Windows)

```bash
dotnet publish src/Fanzi.FanControl/Fanzi.FanControl.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -o ./publish
```

The output in `./publish/` contains a single folder you can copy anywhere and run.

### Option 3 — Open in Visual Studio / Rider

1. Open `FANZI.slnx` in Visual Studio 2022+ or JetBrains Rider.
2. Set `Fanzi.FanControl` as the startup project.
3. Press **Run** / **F5**.

---

## Usage

### Main Dashboard

On launch, FANZi polls all available hardware and displays:

- **CPU card** — package temp, average temp, hotspot, load %, clock MHz, package power, core voltage
- **GPU card** — core temp, hotspot temp, load %, clock MHz, power, VRAM used/total
- **Fan Channels panel** — one row per detected fan with current RPM and a % slider
- **Profiles panel** — tabbed list of saved profiles

### Controlling Fan Speed

1. Locate the fan channel you want to adjust in the **Fan Channels** panel.
2. Drag the slider or type a percentage into the numeric field.
3. Click **Apply** to commit the change to the hardware.
4. Click **Auto** to return the channel to motherboard-managed (automatic) control.

### Fan Profiles

1. Click **+ Add Profile** to create a new named profile.
2. Fill in:
   - **Name** — human-readable label
   - **CPU Fan %** — desired speed for the CPU fan
   - **Warning threshold °C** — temperature at which an email alert fires
   - **Email** — destination address for thermal alerts
   - **Per-channel %** — individual overrides for each detected fan channel
3. Click the profile tab to make it active; the percentages are applied automatically on startup.
4. Settings persist to `%APPDATA%\FANZI\settings.json`.

---

## RGB Lighting Effects

FANZi's built-in effects engine runs at 30 fps and drives all OpenRGB-connected devices.

### Effect Types

| Effect | Description |
|---|---|
| **Static** | Solid colour, no animation |
| **Pulse** | Brightness fades in and out (breathing) |
| **Rainbow** | Full HSV hue rotation across all LEDs |
| **ColorWave** | Colour sweeps across devices left-to-right |
| **TemperatureReactive** | Cool blue at idle → red at thermal limit |
| **CpuLoadReactive** | Speed and brightness follow CPU load % |
| **Performance** | Combines load (speed) with temperature (colour) |
| **Strobe** | Rapid flash |
| **DualColorFlash** | Alternates between two user-chosen colours |

### Built-in Theme Presets

| Preset | Effect | Description |
|---|---|---|
| 🌊 Ocean | Pulse | Deep blue breathing |
| 🔥 Inferno | ColorWave | Scorching rainbow wave |
| ❄ Glacier | Static | Ice-cold static glow |
| ⚡ Neon | DualColorFlash | Cyan/magenta dual flash |
| 🌿 Nature | Pulse | Calm green breathe |
| 🌅 Sunset | ColorWave | Orange to purple palette |
| 🌈 Spectrum | Rainbow | Full HSV rainbow cycle |
| 🩸 Blood Moon | Pulse | Deep crimson pulse |
| 🐧 Arctic | Static | Pure static ice white |
| 🌡 Temp Reactive | TemperatureReactive | Colour follows CPU/GPU temperature |
| 🚀 Performance | Performance | Speed + colour follow real load & temps |
| 💡 Strobe | Strobe | Rapid strobe flash |

### Connecting OpenRGB

1. Download and install [OpenRGB](https://openrgb.org).
2. In OpenRGB, go to **Settings → SDK Server** and click **Start Server**.
3. Launch FANZi — it will automatically connect on port **6742**.

---

## Email Notifications

FANZi can send a thermal-alert email when the CPU temperature exceeds the profile's warning threshold.

Configure SMTP settings in the **Profile** panel:
- SMTP Host (e.g. `smtp.gmail.com`)
- SMTP Port (e.g. `587`)
- SMTP Username / Password
- Recipient email address

Click **Send Test Email** to verify your settings before relying on them.

---

## Project Structure

```
FANZi/
├── FANZI.slnx                        # Solution file
├── FANZI.code-workspace               # VS Code workspace
├── README.md                          # This file
├── CONTRIBUTING.md                    # Contribution guide
├── launchers/
│   ├── FANZI-Windows.bat             # Windows launch script
│   ├── FANZI-Mac.command             # macOS launch script
│   └── FANZI-Linux.sh               # Linux launch script
└── src/
    └── Fanzi.FanControl/
        ├── Fanzi.FanControl.csproj   # Project file & NuGet dependencies
        ├── Program.cs                # Entry point
        ├── App.axaml / App.axaml.cs # Avalonia application root
        ├── Assets/                   # Icons and images
        ├── Models/                   # Plain data / record types
        ├── Services/                 # Business logic & hardware access
        ├── ViewModels/               # MVVM ViewModels (CommunityToolkit)
        └── Views/                    # Avalonia XAML views
```

See **[docs/FILE-STRUCTURE.md](docs/FILE-STRUCTURE.md)** for a detailed description of every file.

---

## Architecture

FANZi follows the **MVVM** (Model-View-ViewModel) pattern:

```
Views (AXAML)
   └── bind to ──► ViewModels (CommunityToolkit.Mvvm)
                       └── call ──► Services (interfaces)
                                       ├── HardwareMonitorService  (LibreHardwareMonitor)
                                       ├── OpenRgbService          (OpenRGB.NET)
                                       ├── SettingsService         (JSON persistence)
                                       └── EmailNotificationService (SMTP)
```

See **[docs/WIKI.md](docs/WIKI.md)** for a deeper architectural walkthrough.

---

## Contributing

Contributions are welcome! Please read **[CONTRIBUTING.md](CONTRIBUTING.md)** before submitting a pull request.

---

## License

Copyright © 2026 **Ionity Global (Pty) Ltd**. All rights reserved.

This software is proprietary. Redistribution or modification without written permission from Ionity Global (Pty) Ltd is not permitted.

---

*Built with ❤️ by Johan Wilhelm van Antwerp — Ionity Global*
