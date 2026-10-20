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
  <img alt="Version" src="https://img.shields.io/badge/version-4.1.1-green"/>
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

**FANZi** (Fan + Zi) is a desktop application for real-time hardware telemetry and fan management. It reads CPU and GPU sensor data via [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), lets you set individual fan channel speeds, and drives RGB lighting across all [OpenRGB](https://openrgb.org)-compatible devices with a rich built-in effects engine.

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
| 🎨 **RGB Studio (v4)** | Colourful effect / theme / swatch buttons, AI colour harmony, device cards with Identify, deep hardware scan |
| 🤖 **AEDi Lighting AI (v4)** | Describe-a-mood lighting, Smart Ambient, Thermal Guardian alert, Night dimming, AEDi suggest — all offline |
| ▣ **Overlay Studio (v4)** | Switchable rows incl. RAM and network ↓/↑, 80–160 % size, 8 accents incl. RGB Sync, snap to corners |
| ⏱ **Live heartbeat (v4)** | Dashboard, Tasks, Network, Power and the overlay update together, in place — no list flicker |
| ⌨ **Typing FX (4.3)** | Keystrokes become light: Ripple, Pulse, Line, Area, Fireworks, Typewriter, Matrix rain, Typing heat — on the keyboard where you hit the key and across RAM, GPU, strips and mouse |
| 🔥 **Thermal Bench (4.3)** | Repeatable all-core cooling benchmark with a 0–100 score, grade, fan-lag / recovery metrics, run history and CSV export |
| ▤ **RAM lighting that sticks (4.3)** | ENE DRAM mirrored on two SMBus ports is driven exactly once, SMBus writes are rate-limited, and **Re-sync RAM** recovers a stuck controller without a reboot |

### What's new in v4.3.1

- **RAM lighting fixed.** On AMD boards OpenRGB lists every ENE DRAM stick twice — once per SMBus host port (PIIX4 0 and 1) at the
  same address. 4.2 and earlier streamed colours through both entries; two host ports writing into one ENE controller corrupt its
  register pointer and the stick ignores every colour until a power-cycle (and reads back 0 / 249 / 257 LEDs). 4.3 drives one entry
  per stick (`SmbusMirrorResolver`), rate-limits SMBus devices to 5 fps, skips unchanged frames and adds **Re-sync RAM** plus a
  **Try SMBus port N** override in the RGB tab.
- **Typing FX.** Eight keystroke-reactive styles rendered per LED at 30 fps. The keyboard uses true key positions (OpenRGB LED names
  / matrix map, with an ANSI layout fallback); with *All devices* on, RAM, GPU, strips, mouse and monitor receive the same animation
  mapped onto their own LEDs. Only key *codes* are polled (GetAsyncKeyState); nothing is logged or sent.
- **Bench tab.** Baseline → all-core load → cooldown, sampled from FANZi's own monitor, scored 0–100 with a plain-language verdict,
  before/after comparison, session history and CSV export. Stops itself at 95 °C.

### What's new in v4.0.0

- **RGB Studio** — the RGB tab is rebuilt: a rotating rainbow orb, outlined cards and buttons painted in their own colours.
- **AEDi Lighting AI** — type a mood ("calm ocean", "fast cyberpunk party", "south africa"), or let Smart Ambient follow
  idle / work / gaming / heat. Thermal Guardian (on by default) flashes every device red when the CPU reaches 92 °C or the GPU 86 °C.
- **Deep scan** — explains exactly which RGB hardware FANZi can and cannot reach and why (RAM sticks vs ENE controllers,
  fan headers vs ARGB controllers, motherboard support, PawnIO, unclaimed USB RGB devices).
- **Overlay Studio** — Settings → Overlay Studio, or right-click the overlay. Title-bar **▣ Overlay** button, Ctrl+O, tray menu.
- **Outlined tabs** and **one synced update cycle** for every live panel.

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

FANZi targets **OpenRGB 1.0** (SDK server on port **6742**).

1. **Automatic:** open the RGB tab. If OpenRGB is not found, FANZi resolves the current Windows portable zip from [openrgb.org/releases.html](https://openrgb.org/releases.html) (with verified pinned fallbacks, HTTPS only), validates it and installs it to `%LOCALAPPDATA%\FANZI\OpenRGB`, then starts the SDK server. The FANZi installer does the same into `<install dir>\OpenRGB`.
2. **Existing install:** FANZi detects OpenRGB via common paths and the Windows Uninstall / App Paths registry keys. If it still can't find it, use **Locate OpenRGB.exe…** in the RGB tab; the path is saved in settings.
3. **Manual:** download OpenRGB yourself, enable **Settings → SDK Server → Start Server**, then click **Retry** in FANZi.

If installation or connection fails, the RGB tab keeps a banner with the exact error plus **Retry**, **Locate OpenRGB.exe…**, **Open download page** and **PawnIO** buttons. Automatic re-download is attempted at most once every 10 minutes.

**PawnIO:** since 1.0rc2, OpenRGB needs the [PawnIO](https://pawnio.eu) driver and Administrator rights to control RAM and motherboard RGB. FANZi runs as Administrator; when PawnIO is missing the RGB tab shows an **Install PawnIO** button that downloads the signed installer from `github.com/namazso/PawnIO.Setup` (Authenticode-verified, signer `namazso.eu`), runs it silently and restarts OpenRGB so the new devices appear. GPUs on NVIDIA NvAPI I2C work without PawnIO.

### How FANZi drives each device (v3.0)

Each device row in the RGB tab shows how it is controlled:

| Control | Devices | How colours are sent |
|---|---|---|
| **Per-LED** (Direct / Custom) | Motherboards, RAM, LED strips, monitors such as the ViewSonic XG270QG | Switched to the per-LED mode, then every frame goes out with `UpdateLeds`. |
| **Single colour** (Static) | GPUs with no per-LED mode, for example the ZOTAC RTX 30-series | The frame's average colour is written into the Static mode, at most every 120 ms and only when it changes. |
| **Effects only** | Devices that expose no colour mode | Listed, not coloured. |
| **Mirror** (4.3) | The same SMBus controller seen through a second host port | Listed, never written — see *RAM lighting* below. |

**RAM lighting (4.3):** the RGB tab shows a *RAM LIGHTING* line — which SMBus port drives the sticks and how many mirror entries were
skipped. **Re-sync RAM** re-applies Direct mode and re-sends the colour (the fix for "RAM stopped changing colour"); **Try SMBus port N**
flips the automatic port choice if colours still don't land; **Auto** returns to it. SMBus-attached devices receive at most one frame per
200 ms and never an unchanged frame.

Two OpenRGB 1.0 compatibility fixes make this work:

- **Mode packets:** OpenRGB.NET 3.1.1 sends direction `0xFFFFFFFF` for modes without a direction, and OpenRGB 1.0 rejects those packets silently. FANZi sends mode changes itself (`OpenRgbModeWriter`, SDK protocol 4) with direction `0`, and verifies every switch by reading the device back.
- **Server settings:** FANZi starts OpenRGB with a private config (`%LOCALAPPDATA%\FANZI\OpenRGB\config`) that enables `Server → legacy_workaround`. It also turns that flag on in `%APPDATA%\OpenRGB\OpenRGB.json`, keeping a `.fanzi-backup` copy. If a running server still refuses mode changes, FANZi restarts it once with these settings.

**Other RGB software:** SignalRGB, iCUE, Armoury Crate / Aura, MSI Center, RGB Fusion, Razer Synapse, NZXT CAM, G HUB, L-Connect, ZOTAC FireStorm / Spectra and ASRock Polychrome compete for the same hardware. When one is running, the RGB tab names it and offers **Close them**. That button closes those apps and rescans.

**Hardware check:** `$env:FANZI_HARDWARE_TESTS=1; dotnet test tests\Fanzi.FanControl.Tests --filter Category=Hardware` starts OpenRGB and sets every device to red, green and blue. An independent SDK client then confirms what OpenRGB applied, and the results are written to `%TEMP%\fanzi-rgb-live-report.txt`.

**Logs:** FANZi writes a daily log to `%APPDATA%\FANZI\logs\fanzi-yyyyMMdd.log` (kept 7 days), including every OpenRGB download, install and connection attempt. Crash logs are in `%APPDATA%\FANZI\crash-logs`.

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
