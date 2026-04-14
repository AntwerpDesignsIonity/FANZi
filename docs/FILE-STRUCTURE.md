# FANZi — File Structure Reference

This document describes every file and directory in the FANZi repository and its role in the project.

---

```
FANZi/
├── .gitignore
├── CONTRIBUTING.md
├── FANZI.code-workspace
├── FANZI.slnx
├── README.md
├── docs/
│   ├── FILE-STRUCTURE.md          ← this file
│   └── WIKI.md
├── .github/
│   ├── pull_request_template.md
│   └── ISSUE_TEMPLATE/
│       ├── bug_report.md
│       └── feature_request.md
├── launchers/
│   ├── FANZI-Linux.sh
│   ├── FANZI-Mac.command
│   └── FANZI-Windows.bat
└── src/
    └── Fanzi.FanControl/
        ├── Fanzi.FanControl.csproj
        ├── app.manifest
        ├── App.axaml
        ├── App.axaml.cs
        ├── Program.cs
        ├── ViewLocator.cs
        ├── Assets/
        │   ├── avalonia-logo.ico
        │   ├── ionity-logo.ico
        │   └── ionity-logo.png
        ├── Converters/
        ├── Models/
        │   ├── AppSettings.cs
        │   ├── CpuReadingSnapshot.cs
        │   ├── FanChannelSnapshot.cs
        │   ├── FanProfile.cs
        │   ├── GpuReadingSnapshot.cs
        │   ├── HardwareSnapshot.cs
        │   ├── RgbColor.cs
        │   ├── RgbDeviceInfo.cs
        │   ├── RgbEffectType.cs
        │   ├── RgbThemePreset.cs
        │   └── SetFanControlResult.cs
        ├── Services/
        │   ├── EmailNotificationService.cs
        │   ├── HardwareMonitorService.cs
        │   ├── IHardwareMonitorService.cs
        │   ├── IRgbService.cs
        │   ├── ISettingsService.cs
        │   ├── OpenRgbService.cs
        │   ├── RgbEffectsEngine.cs
        │   └── SettingsService.cs
        ├── ViewModels/
        │   ├── FanChannelViewModel.cs
        │   ├── MainWindowViewModel.cs
        │   ├── ProfileTabViewModel.cs
        │   ├── RgbControlViewModel.cs
        │   └── ViewModelBase.cs
        └── Views/
            ├── MainWindow.axaml
            ├── MainWindow.axaml.cs
            ├── RgbControlView.axaml
            └── RgbControlView.axaml.cs
```

---

## Repository Root

| File / Directory | Description |
|---|---|
| `.gitignore` | Excludes `bin/`, `obj/`, IDE artefacts, NuGet caches, and OS files from version control. |
| `CONTRIBUTING.md` | Contribution guide: setup, coding standards, commit conventions, PR process. |
| `FANZI.code-workspace` | VS Code multi-root workspace configuration. |
| `FANZI.slnx` | Solution file (new XML-based `.slnx` format) referencing the single `Fanzi.FanControl` project. |
| `README.md` | Main project documentation: overview, features, requirements, installation, usage. |

---

## docs/

Project documentation files intended for developers.

| File | Description |
|---|---|
| `docs/FILE-STRUCTURE.md` | This document. Describes the purpose of every file and directory. |
| `docs/WIKI.md` | In-depth wiki: architecture, all layers (Models/Services/ViewModels/Views), algorithms, FAQ. |

---

## .github/

GitHub-specific configuration files.

| File | Description |
|---|---|
| `.github/pull_request_template.md` | Template pre-filled when a contributor opens a PR. |
| `.github/ISSUE_TEMPLATE/bug_report.md` | Structured bug report template. |
| `.github/ISSUE_TEMPLATE/feature_request.md` | Structured feature request template. |

---

## launchers/

Convenience scripts that invoke `dotnet run` from the repository root. They all run FANZi in Release configuration.

| File | Platform | Notes |
|---|---|---|
| `FANZI-Windows.bat` | Windows | Batch file; `cd /d "%~dp0\.."` resolves repo root correctly regardless of where the script is invoked from. Ends with `pause` so the window stays open if dotnet fails. |
| `FANZI-Mac.command` | macOS | Bash script; double-clickable from Finder. Uses `$(dirname "$0")` to resolve the repo root. |
| `FANZI-Linux.sh` | Linux | Identical logic to the Mac launcher. Mark executable with `chmod +x`. |

---

## src/Fanzi.FanControl/

The single C# project that contains the entire application.

### Project File

| File | Description |
|---|---|
| `Fanzi.FanControl.csproj` | MSBuild project file. Targets `net10.0`, sets `OutputType=WinExe`, declares all NuGet package references (Avalonia, CommunityToolkit.Mvvm, LibreHardwareMonitorLib, OpenRGB.NET), and configures application metadata (version, company, icon). |
| `app.manifest` | Windows application manifest. Declares UAC execution level and Windows 10/11 compatibility GUIDs. |

### Application Bootstrap

| File | Description |
|---|---|
| `Program.cs` | Entry point. Calls `AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace().StartWithClassicDesktopLifetime(args)`. |
| `App.axaml` | Avalonia application XAML root. Applies `FluentTheme` and registers the `ViewLocator` as a `DataTemplate`. |
| `App.axaml.cs` | Code-behind for `App`. Constructs the service instances (`HardwareMonitorService`, `OpenRgbService`, `SettingsService`), creates `MainWindowViewModel`, sets the main window, and wires up the `Exit` handler for clean disposal. Also disables duplicate validation from Avalonia and CommunityToolkit. |
| `ViewLocator.cs` | Avalonia `IDataTemplate` implementation. Resolves a ViewModel to its matching View by convention (`…ViewModel` → `…View`). |

---

### Assets/

Static files embedded into the application binary as Avalonia resources.

| File | Description |
|---|---|
| `ionity-logo.ico` | Application icon used in the taskbar, title bar, and Windows Explorer. |
| `ionity-logo.png` | Ionity Global logo used in the README and any in-app splash. |
| `avalonia-logo.ico` | Default Avalonia template icon (retained for fallback). |

---

### Converters/

Value converters for Avalonia data bindings (e.g., bool → visibility, null → string). These bridge ViewModel data types to XAML display needs without putting logic in the View.

---

### Models/

Plain data containers. No business logic, no UI dependencies.

| File | Description |
|---|---|
| `AppSettings.cs` | Root settings object: list of `FanProfile` and the active profile ID. |
| `CpuReadingSnapshot.cs` | Per-core snapshot: label, clock MHz, load %, temperature. |
| `FanChannelSnapshot.cs` | One fan channel: ID, name, RPM, current %, controllable flag. |
| `FanProfile.cs` | A named fan profile with per-channel speed overrides and alert settings. |
| `GpuReadingSnapshot.cs` | Per-GPU sensor snapshot: label, clock MHz, load %, temperature. |
| `HardwareSnapshot.cs` | Full point-in-time hardware reading aggregating CPU, GPU, and fan data. |
| `RgbColor.cs` | Immutable `(byte R, G, B)` value type with named colour constants. |
| `RgbDeviceInfo.cs` | Describes one OpenRGB device: name, type, LED count. |
| `RgbEffectType.cs` | Enum of all supported lighting effects (Static, Pulse, Rainbow, …). |
| `RgbThemePreset.cs` | Named preset bundling effect + colours + speed + brightness. Contains 12 built-in static presets and an `All` catalogue list. |
| `SetFanControlResult.cs` | Operation result for fan-control calls: `Success` bool + `Message` string. |

---

### Services/

Business logic and hardware integration. All stateful services are disposed via the `App.axaml.cs` `Exit` handler.

| File | Description |
|---|---|
| `IHardwareMonitorService.cs` | Interface: `GetSnapshotAsync`, `SetFanControlAsync`, `RestoreAutomaticControlAsync`. |
| `HardwareMonitorService.cs` | Implementation using LibreHardwareMonitor. Windows-only; gracefully no-ops on other platforms. Thread-safe via `lock (_syncRoot)`. |
| `IRgbService.cs` | Interface: connect, enumerate devices, set colours, disconnect. |
| `OpenRgbService.cs` | Implementation using `OpenRGB.NET`. Connects to the OpenRGB SDK server. All methods are no-ops when not connected. |
| `RgbEffectsEngine.cs` | Pure static class. `Tick(...)` computes the `RgbColor` for one device at a given elapsed time. Stateless — safe to call from any thread. |
| `ISettingsService.cs` | Interface: `LoadAsync` / `SaveAsync` for `AppSettings`. |
| `SettingsService.cs` | JSON implementation using `System.Text.Json`. Atomic write via `.tmp` file swap. Settings path: `%APPDATA%\FANZI\settings.json`. |
| `EmailNotificationService.cs` | SMTP email sender using `System.Net.Mail`. SSL always enabled. Returns human-readable result strings. |

---

### ViewModels/

MVVM ViewModels. All use [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) source generators (`[ObservableProperty]`, `[RelayCommand]`).

| File | Description |
|---|---|
| `ViewModelBase.cs` | Base class inheriting `ObservableObject` from CommunityToolkit.Mvvm. |
| `MainWindowViewModel.cs` | Root ViewModel. Owns the hardware polling loop, fan channel collection, profile list, and coordinates child ViewModels. Implements `IDisposable`. |
| `FanChannelViewModel.cs` | Wraps a `FanChannelSnapshot`. Exposes `DesiredPercent` slider value plus `ApplyCommand` (calls `SetFanControlAsync`) and `AutoCommand` (calls `RestoreAutomaticControlAsync`). |
| `ProfileTabViewModel.cs` | Wraps a `FanProfile`. Exposes all editable profile fields and saves on changes. |
| `RgbControlViewModel.cs` | Drives the RGB pipeline at 30 fps. Owns the `System.Timers.Timer`, calls `RgbEffectsEngine`, pushes colours to `IRgbService`. Exposes all lighting settings as observable properties. Implements `IDisposable`. |

---

### Views/

Avalonia AXAML user interface files. Each view has a paired code-behind (`.axaml.cs`). Views contain **no business logic** — only layout, style, and data-binding declarations.

| File | Description |
|---|---|
| `MainWindow.axaml` | Primary application window. Dark navy theme. TabControl with Dashboard and RGB tabs. Dashboard contains CPU card, GPU card, Fan Channels panel, and Profiles panel. |
| `MainWindow.axaml.cs` | Code-behind. Wires the `Closed` event to call `Dispose()` on the ViewModel. |
| `RgbControlView.axaml` | RGB control panel. Effect type picker, theme preset list, colour sliders, hex inputs, speed/brightness controls, device status. |
| `RgbControlView.axaml.cs` | Code-behind. Minimal — only `InitializeComponent()`. |
