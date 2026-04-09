@echo off
title FANZI - Fan Control
cd /d "%~dp0\.."
dotnet run --project src/Fanzi.FanControl/Fanzi.FanControl.csproj --configuration Release
pause
