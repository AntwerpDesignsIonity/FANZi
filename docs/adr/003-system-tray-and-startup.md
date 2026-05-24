# ADR-003: System Tray Integration and Windows Startup

## Status
Accepted

## Context
Fan control software must run continuously. Users expect:
- Minimize to tray (not taskbar clutter)
- Close button minimizes rather than exits
- Auto-start with Windows
- Reduced resource usage when hidden

## Decision
- Use Avalonia's `ShutdownMode.OnExplicitShutdown` to prevent exit on window close
- Hide window on minimize/close (configurable)
- Register `HKCU\Run` key for startup with `--minimized` flag
- Adaptive polling: 3s when visible, 10s when hidden (3.3x less CPU usage)
- Single-instance mutex prevents duplicate processes

## Consequences
- App stays running in background consuming minimal resources
- Users can fully exit via tray context menu or Settings
- Startup registration is per-user (HKCU), no admin required for that specific operation
- Process detection via mutex means clicking the app icon while running does nothing (no second instance)
