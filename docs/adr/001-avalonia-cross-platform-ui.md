# ADR-001: Avalonia UI as Cross-Platform Framework

## Status
Accepted

## Context
FANZI needs a desktop UI framework that runs on Windows (primary), macOS, and Linux while providing native-feeling performance and a modern dark-theme aesthetic. Options considered:
- **WPF** — Windows-only, mature
- **MAUI** — Cross-platform but unstable on desktop, heavy runtime
- **Avalonia** — Cross-platform XAML, lightweight, active community
- **Electron** — Large bundle, high memory usage

## Decision
Use **Avalonia 11.x** with Fluent theme.

## Consequences
- Single XAML codebase renders on all desktop platforms
- Compiled bindings provide compile-time safety
- Hardware access (LibreHardwareMonitor) is Windows-only; UI gracefully degrades elsewhere
- Smaller bundle size (~50MB self-contained) vs Electron (~150MB+)
- Custom window chrome via ExtendClientAreaToDecorationsHint for branded titlebar
