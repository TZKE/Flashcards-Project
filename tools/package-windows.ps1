# =============================================================================
# OrbitLab by StarshipAI — Windows packaging script (Phase 6A foundation)
# =============================================================================
# Publishes the WPF app for win-x64 and, when the Velopack CLI (vpk) is
# installed, builds a user-friendly Windows installer + update package.
#
# Output goes to artifacts\installer\ (gitignored). This script:
#   - never touches the backend,
#   - never includes secrets, local databases, or any user/AppData research data
#     (it packages ONLY the dotnet publish output of the app itself),
#   - never uploads anything anywhere. Distribution stays manual until the
#     public domain + HTTPS update feed exist.
#
# Prereqs:  .NET 8 SDK.  Optional: Velopack CLI ->  dotnet tool install -g vpk
# Usage:    powershell -ExecutionPolicy Bypass -File tools\package-windows.ps1
#           (optional)  -Version 0.9.1
# =============================================================================
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repoRoot "FlashcardMaker.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish-win-x64"
$installerDir = Join-Path $repoRoot "artifacts\installer"

# --- resolve version (default: <Version> from the csproj) -------------------
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw "Could not resolve a version. Pass -Version x.y.z." }
Write-Host "==> Packaging OrbitLab v$Version (win-x64)" -ForegroundColor Cyan

# --- clean output dirs (repo artifacts only — never user data) --------------
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force $installerDir | Out-Null

# --- publish -----------------------------------------------------------------
# Self-contained so testers do not need the .NET runtime preinstalled.
Write-Host "==> dotnet publish (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $publishDir -v m
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Safety: the publish folder must not contain local config/DB/user files.
$suspicious = Get-ChildItem $publishDir -File |
    Where-Object { $_.Name -match "orbitlab\.settings\.json|\.db$|accounts\.json|settings\.json|research_projects" }
foreach ($f in $suspicious) {
    Write-Warning "Removing non-distributable file from publish output: $($f.Name)"
    Remove-Item $f.FullName -Force
}

# --- package -----------------------------------------------------------------
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if ($vpk) {
    Write-Host "==> Velopack: building installer + update package..." -ForegroundColor Cyan
    vpk pack `
        --packId "OrbitLab" `
        --packTitle "OrbitLab by StarshipAI" `
        --packAuthors "StarshipAI" `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe "AI-Flashcard-Maker.exe" `
        --icon (Join-Path $repoRoot "Assets\appicon.ico") `
        --outputDir $installerDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }
    Write-Host "==> Done. Installer + update files in artifacts\installer\" -ForegroundColor Green
    Write-Host "    NOTE: builds are NOT code-signed yet - Windows SmartScreen will warn testers." -ForegroundColor Yellow
    Write-Host "    Do NOT publish these files publicly until domain + HTTPS exist." -ForegroundColor Yellow
} else {
    Write-Warning "Velopack CLI (vpk) not found - no installer was built."
    Write-Warning "Install it with:  dotnet tool install -g vpk   then re-run this script."
    Write-Host "==> Fallback: creating plain zip for manual local testing..." -ForegroundColor Cyan
    $zipPath = Join-Path $installerDir "OrbitLab-win-x64-v$Version-manual-test.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
    Write-Host "==> Done. Zip (NOT an installer): $zipPath" -ForegroundColor Green
}

# --- SHA256 for the release admin page ---------------------------------------
Write-Host "==> SHA256 hashes (paste into the admin Releases page when distributing):" -ForegroundColor Cyan
Get-ChildItem $installerDir -File | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "    $($_.Name)  $h"
}
