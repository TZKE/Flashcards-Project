# =============================================================================
# OrbitLab by StarshipAI — Windows packaging script (Phase 6A.1)
# =============================================================================
# Publishes the WPF app for win-x64 and builds a real Windows installer +
# update package with Velopack. Output goes to artifacts\installer\ (gitignored).
#
# This script:
#   - never touches the backend,
#   - never uploads anything anywhere (distribution stays manual until the
#     public domain + HTTPS update feed exist),
#   - packages ONLY the dotnet publish output of the app itself — no AppData,
#     no local settings, no local databases, no research projects/autosave/
#     lastgood files, no API keys, no secrets, no user files, no scratchpad,
#     and a guard below fails the build if anything suspicious slips in.
#
# Prereqs:  .NET 8 SDK.
# Velopack: preferred via the repo-local dotnet tool manifest
#           (.config/dotnet-tools.json):  dotnet tool restore
#           A global `vpk` install also works. Without either, the script
#           falls back to a plain zip (manual testing only, NOT an installer).
#
# Usage:    powershell -ExecutionPolicy Bypass -File tools\package-windows.ps1
#           (optional)  -Version 0.1.0-beta.1   (default: <Version> from csproj)
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
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw "Could not resolve a version. Pass -Version x.y.z[-beta.N]." }
Write-Host "==> Packaging OrbitLab v$Version (win-x64)" -ForegroundColor Cyan

# --- clean previous packaging output (repo artifacts only — never user data) --
foreach ($dir in @($publishDir, $installerDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
New-Item -ItemType Directory -Force $installerDir | Out-Null

# --- publish -----------------------------------------------------------------
# Self-contained so testers do not need the .NET runtime preinstalled.
Write-Host "==> dotnet publish (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $publishDir -v m
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# --- hard guard: nothing private may ship ------------------------------------
# The publish folder must contain only app binaries/assets. Fail loudly if any
# local config, database, key material, or research/user data pattern appears.
$forbidden = Get-ChildItem $publishDir -Recurse -File | Where-Object {
    $_.Name -match "orbitlab\.settings\.json|\.db$|\.db-wal$|\.db-shm$|accounts\.json|^settings\.json$|research_projects|research_data|bootstrap\.cache|crash\.log|apikey|api_key|secret|credential"
}
if ($forbidden) {
    $forbidden | ForEach-Object { Write-Warning "FORBIDDEN FILE IN PUBLISH OUTPUT: $($_.FullName)" }
    throw "Packaging aborted: private/user files detected in publish output."
}
Write-Host "==> Publish output clean: no settings/db/user/research files." -ForegroundColor Green

# --- locate Velopack CLI: local tool manifest first, then global vpk ---------
$vpkInvoke = $null
Push-Location $repoRoot
try {
    if (Test-Path (Join-Path $repoRoot ".config\dotnet-tools.json")) {
        dotnet tool restore | Out-Null
        dotnet tool run vpk -- --help *> $null
        if ($LASTEXITCODE -eq 0) { $vpkInvoke = "local" }
    }
    if (-not $vpkInvoke) {
        $global = Get-Command vpk -ErrorAction SilentlyContinue
        if ($global) { $vpkInvoke = "global" }
    }

    # --- package ------------------------------------------------------------
    if ($vpkInvoke) {
        Write-Host "==> Velopack ($vpkInvoke tool): building installer + update package..." -ForegroundColor Cyan
        $vpkArgs = @(
            "pack",
            "--packId", "OrbitLab",
            "--packTitle", "OrbitLab by StarshipAI",
            "--packAuthors", "StarshipAI",
            "--packVersion", $Version,
            "--packDir", $publishDir,
            "--mainExe", "AI-Flashcard-Maker.exe",
            "--icon", (Join-Path $repoRoot "Assets\appicon.ico"),
            "--outputDir", $installerDir
        )
        if ($vpkInvoke -eq "local") { dotnet tool run vpk -- @vpkArgs } else { vpk @vpkArgs }
        if ($LASTEXITCODE -ne 0) { throw "vpk pack failed (exit $LASTEXITCODE)." }
        Write-Host "==> Done. Installer + update files in artifacts\installer\" -ForegroundColor Green
        Write-Host "    NOTE: builds are NOT code-signed yet - Windows SmartScreen will warn testers." -ForegroundColor Yellow
        Write-Host "    Do NOT publish these files publicly until domain + HTTPS exist." -ForegroundColor Yellow
    } else {
        Write-Warning "Velopack CLI not found (no local manifest tool, no global vpk) - no installer was built."
        Write-Warning "Preferred fix:  dotnet tool restore   (repo has a tool manifest)"
        Write-Host "==> Fallback: creating plain zip for manual local testing (NOT an installer)..." -ForegroundColor Cyan
        $zipPath = Join-Path $installerDir "OrbitLab-win-x64-v$Version-manual-test.zip"
        Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
        Write-Host "==> Done. Zip: $zipPath" -ForegroundColor Green
    }
}
finally { Pop-Location }

# --- SHA256 for the release admin page ---------------------------------------
Write-Host "==> SHA256 hashes (paste the installer hash into the admin Releases page):" -ForegroundColor Cyan
Get-ChildItem $installerDir -File | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "    $($_.Name)  $h"
}
