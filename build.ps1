# FANZI Build & Publish Script
# Usage: .\build.ps1 [-Runtime win-x64|win-arm64|linux-x64|osx-x64] [-Installer]

param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "osx-x64", "all")]
    [string]$Runtime = "win-x64",
    [switch]$Installer,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$ProjectPath = "$PSScriptRoot\src\Fanzi.FanControl\Fanzi.FanControl.csproj"
$PublishBase = "$PSScriptRoot\publish"

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Blue
Write-Host "  ║     FANZI v2.0 — Build System        ║" -ForegroundColor Blue
Write-Host "  ║   Ionity Global (Pty) Ltd            ║" -ForegroundColor DarkGray
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Blue
Write-Host ""

if ($Clean) {
    Write-Host "[CLEAN] Removing publish/ and bin/obj..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force "$PublishBase" -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force "$PSScriptRoot\src\Fanzi.FanControl\bin" -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force "$PSScriptRoot\src\Fanzi.FanControl\obj" -ErrorAction SilentlyContinue
}

function Publish-Runtime {
    param([string]$Rid)

    $outDir = "$PublishBase\$Rid"
    Write-Host "[BUILD] Publishing $Rid..." -ForegroundColor Cyan

    $args = @(
        "publish", $ProjectPath,
        "-c", "Release",
        "-r", $Rid,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-o", $outDir
    )

    if ($Rid.StartsWith("win")) {
        $args += "-p:PublishReadyToRun=true"
    }

    & dotnet @args

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Build failed for $Rid" -ForegroundColor Red
        exit 1
    }

    $exe = Get-ChildItem "$outDir\Fanzi.FanControl*" | Where-Object { $_.Extension -in ".exe", "" } | Select-Object -First 1
    if ($exe) {
        $sizeMB = [math]::Round($exe.Length / 1MB, 1)
        Write-Host "[DONE] $Rid -> $($exe.Name) ($sizeMB MB)" -ForegroundColor Green
    }
}

if ($Runtime -eq "all") {
    @("win-x64", "win-arm64", "linux-x64", "osx-x64") | ForEach-Object { Publish-Runtime $_ }
} else {
    Publish-Runtime $Runtime
}

if ($Installer) {
    Write-Host ""
    Write-Host "[INSTALLER] Building Windows installer..." -ForegroundColor Cyan
    $innoPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $innoPath)) {
        $innoPath = "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    }
    if (Test-Path $innoPath) {
        & $innoPath "$PSScriptRoot\installer\FANZI-Setup.iss"
        if ($LASTEXITCODE -eq 0) {
            Write-Host "[DONE] Installer created in publish\installer\" -ForegroundColor Green
        }
    } else {
        Write-Host "[SKIP] Inno Setup 6 not found. Install from https://jrsoftware.org/isinfo.php" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "  Build complete!" -ForegroundColor Green
Write-Host ""
