# ADR-004: Crash Recovery and Auto-Update

## Status
Accepted

## Context
Fan control crashing can leave fans at fixed duty (dangerous for thermals). We need:
- Graceful crash logging for debugging
- Auto-restart on unhandled exceptions
- Version checking against GitHub releases

## Decision
**CrashGuardService:**
- Hooks `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`
- Writes structured crash logs to `%APPDATA%\FANZI\crash-logs\`
- Auto-restarts up to 3 times on terminal crashes
- 7-day log rotation

**AutoUpdateService:**
- Checks `api.github.com/repos/{owner}/{repo}/releases/latest`
- Compares semver against compiled version
- Downloads installer EXE to temp directory
- User initiates the actual update (no silent install)

## Consequences
- Crash logs enable remote debugging without reproduction
- Auto-restart keeps fan control active even after errors
- 3-attempt limit prevents restart loops on persistent crashes
- Update check is opt-in, never blocks startup
- No telemetry sent — only reads public GitHub API
