#!/bin/bash
# FANZI - Fan Control Launcher (macOS)
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/.."
dotnet run --project src/Fanzi.FanControl/Fanzi.FanControl.csproj --configuration Release
