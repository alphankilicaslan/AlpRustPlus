#!/usr/bin/env pwsh
<#
.SYNOPSIS
    AlpRust+ — Build & Package Script
    Usage: .\build-installer.ps1
    Output: RustPlusDesktop\bin\Installer\AlpRust+-Setup.exe
#>

param(
    [string]$Version = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

function Log([string]$msg) { Write-Host "[BUILD] $msg" -ForegroundColor Cyan }
function Success([string]$msg) { Write-Host "[OK] $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "[ERROR] $msg" -ForegroundColor Red; exit 1 }

# ── 1. KILL RUNNING INSTANCE ────────────────────────────────────────────────
Log "Stopping any running RustPlusDesk instance..."
Stop-Process -Name "RustPlusDesk" -Force -ErrorAction SilentlyContinue
Start-Sleep 1

# ── 2. RESTORE ───────────────────────────────────────────────────────────────
Log "Restoring NuGet packages..."
dotnet restore "$Root\RustPlusDesktop\RustPlusDesk.csproj" --runtime win-x64 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "dotnet restore failed." }
Success "Restore complete."

# ── 3. PUBLISH ───────────────────────────────────────────────────────────────
if (-not $SkipPublish) {
    Log "Publishing (self-contained win-x64)..."
    dotnet publish "$Root\RustPlusDesktop\RustPlusDesk.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishDir="$Root\RustPlusDesktop\bin\Installer\publish" `
        --no-restore

    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed." }
    Success "Publish complete."
} else {
    Log "Skipping publish (--SkipPublish flag set)."
}

# ── 4. VERIFY PUBLISH OUTPUT ─────────────────────────────────────────────────
$PublishDir = "$Root\RustPlusDesktop\bin\Installer\publish"
$ExePath    = "$PublishDir\RustPlusDesk.exe"

if (-not (Test-Path $ExePath)) {
    Fail "RustPlusDesk.exe not found in $PublishDir. Run without -SkipPublish first."
}

# ── 5. VERSION DETECTION ─────────────────────────────────────────────────────
if (-not $Version) {
    $vInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
    $Version = "$($vInfo.FileMajorPart).$($vInfo.FileMinorPart).$($vInfo.FileBuildPart)"
}
Log "Version: $Version"

# ── 6. FIND INNO SETUP ────────────────────────────────────────────────────────
$InnoPaths = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$ISCC = $InnoPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $ISCC) {
    Write-Host ""
    Write-Host "⚠  Inno Setup 6 bulunamadı." -ForegroundColor Yellow
    Write-Host "   Lütfen https://jrsoftware.org/isdl.php adresinden indirip kurun." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "   Kurulumdan sonra bu scripti tekrar çalıştırın:" -ForegroundColor White
    Write-Host "   .\RustPlusDesktop\Installer\build-installer.ps1" -ForegroundColor White
    Write-Host ""
    Write-Host "   Alternatif: Inno Setup GUI'yi açın ve şu dosyayı derleyin:" -ForegroundColor White
    Write-Host "   $Root\RustPlusDesktop\Installer\Setup.iss" -ForegroundColor White
    exit 0
}

# ── 7. COMPILE INSTALLER ─────────────────────────────────────────────────────
$IssFile  = "$Root\RustPlusDesktop\Installer\Setup.iss"
$OutDir   = "$Root\RustPlusDesktop\bin\Installer"

Log "Compiling installer with Inno Setup..."
& $ISCC $IssFile /DMyAppVersion=$Version /O"$OutDir"

if ($LASTEXITCODE -ne 0) { Fail "Inno Setup compilation failed." }

$SetupExe = "$OutDir\AlpRust+-Setup.exe"
if (Test-Path $SetupExe) {
    $size = [math]::Round((Get-Item $SetupExe).Length / 1MB, 1)
    Write-Host ""
    Success "Installer hazır: $SetupExe  ($size MB)"
    Write-Host ""
} else {
    Fail "Setup EXE not found after compile."
}
