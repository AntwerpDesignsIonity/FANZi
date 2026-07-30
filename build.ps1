# FANZI Build & Publish Script
# Usage: .\build.ps1 [-Runtime win-x64|win-arm64|linux-x64|osx-x64|all] [-Installer] [-Clean]

param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "osx-x64", "all")]
    [string]$Runtime = "win-x64",
    [switch]$Installer,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$ProjectPath = "$PSScriptRoot\src\Fanzi.FanControl\Fanzi.FanControl.csproj"
$InstallerProject = "$PSScriptRoot\src\Fanzi.Installer\Fanzi.Installer.csproj"
$PublishBase = "$PSScriptRoot\publish"

Write-Host ""
Write-Host "  ╔══════════════════════════════════════════╗" -ForegroundColor Blue
Write-Host "  ║      FANZI v2.1 — Build System           ║" -ForegroundColor Blue
Write-Host "  ║    Ionity Global Pty Ltd                 ║" -ForegroundColor DarkGray
Write-Host "  ╚══════════════════════════════════════════╝" -ForegroundColor Blue
Write-Host ""

if ($Clean) {
    Write-Host "[CLEAN] Removing publish/ and bin/obj..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force "$PublishBase" -ErrorAction SilentlyContinue
    Get-ChildItem "$PSScriptRoot\src" -Recurse -Directory | Where-Object { $_.Name -in "bin","obj" } | Remove-Item -Recurse -Force
}

function Publish-Runtime {
    param([string]$Rid)

    $outDir = "$PublishBase\$Rid"
    Write-Host "[BUILD] Publishing FANZI for $Rid..." -ForegroundColor Cyan

    $publishArgs = @(
        "publish", $ProjectPath,
        "-c", "Release",
        "-r", $Rid,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-o", $outDir
    )

    if ($Rid.StartsWith("win")) {
        $publishArgs += "-p:PublishReadyToRun=true"
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Build failed for $Rid" -ForegroundColor Red
        exit 1
    }

    $exe = Get-ChildItem "$outDir\Fanzi.FanControl*" | Where-Object { $_.Extension -in ".exe", "" } | Select-Object -First 1
    if ($exe) {
        $sizeMB = [math]::Round($exe.Length / 1MB, 1)
        Write-Host "[DONE] $Rid -> $($exe.Name) - $sizeMB MB" -ForegroundColor Green
    }
}

# Build main app
if ($Runtime -eq "all") {
    @("win-x64", "win-arm64", "linux-x64", "osx-x64") | ForEach-Object { Publish-Runtime $_ }
} else {
    Publish-Runtime $Runtime
}

# Build installer EXE
if ($Installer) {
    Write-Host ""
    Write-Host "[INSTALLER] Building FANZI-Installer.exe..." -ForegroundColor Cyan

    $installerOut = "$PublishBase\installer"

    & dotnet publish $InstallerProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -o $installerOut
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Installer build failed" -ForegroundColor Red
        exit 1
    }

    # Bundle app files alongside installer
    $appBundle = "$installerOut\app"
    if (Test-Path "$PublishBase\win-x64") {
        Write-Host "[BUNDLE] Packaging app files with installer..." -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $appBundle | Out-Null
        Copy-Item "$PublishBase\win-x64\*" $appBundle -Recurse -Force
    }

    $installerExe = Get-ChildItem "$installerOut\FANZI-Installer*" | Select-Object -First 1
    if ($installerExe) {
        $sizeMB = [math]::Round($installerExe.Length / 1MB, 1)
        Write-Host "[DONE] Installer -> $($installerExe.Name) - $sizeMB MB" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "  Installer output: $installerOut" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  Build complete!" -ForegroundColor Green
Write-Host "  Output: $PublishBase" -ForegroundColor Gray
Write-Host ""
