#Requires -Version 5.1
<#
.SYNOPSIS
    Packages Uniwill Control Utility for release: publish, stage native payload,
    Velopack pack (per-machine MSI + full/delta nupkgs), portable zip.

.DESCRIPTION
    Produces the GitHub release assets for jgudec/Uniwill-Control-Utility:
      Releases\Uniwill-Control-Utility.msi            per-machine installer (Program Files)
      Releases\Uniwill-Control-Utility-<ver>-full.nupkg   Velopack full package
      Releases\Uniwill-Control-Utility-<ver>-delta.nupkg  Velopack delta from the previous release
      Releases\releases.win.json                      Velopack GitHub feed index
      Releases\Uniwill-Control-Utility-<ver>-win-x64.zip  portable (no auto-update)

    The version is read from the csproj (single source of truth) — bump it there,
    tag the GitHub release with the same version, upload the printed manifest.

    The Releases\ directory is persistent: deltas are built from the newest full
    nupkg it contains, so keep it in sync with GitHub (step 4 seeds it).

.EXAMPLE
    dotnet tool restore
    .\build\pack.ps1
#>
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$projDir = Join-Path $root "Uniwill Control Utility"
$proj = Join-Path $projDir "Uniwill Control Utility.csproj"
$publishDir = Join-Path $root "artifacts\publish"
$releasesDir = Join-Path $root "Releases"

# --- 1. Version from the csproj (single source of truth) ---
$version = (dotnet msbuild $proj -getProperty:Version | Select-Object -Last 1).Trim()
if (-not $version) { throw "Could not read <Version> from $proj" }
Write-Host "Packaging UCU v$version" -ForegroundColor Cyan

# --- 2. Publish (framework-dependent, NOT single-file) ---
# PublishSingleFile=false is mandatory: native DLLs (ADLX, Magpie, kernel32,
# RTSS) are P/Invoked from the exe directory and break under single-file
# extraction. The MSI ensures the .NET 10 Desktop Runtime via -f below.
dotnet publish $proj -c Release -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# --- 2b. Verify the crash guardian flowed into the layout ---
# The service exe ships in the Service\ subfolder (csproj staging target) so
# the install root shows a single app exe; the "UCU Guardian" scheduled task
# points at it.
foreach ($file in @(
    "Service\Uniwill Control Utility Service.exe", "Service\Uniwill Control Utility Service.dll",
    "Service\Uniwill Control Utility Service.deps.json", "Service\Uniwill Control Utility Service.runtimeconfig.json",
    "Service\Serilog.dll", "Service\Serilog.Sinks.File.dll"
)) {
    if (-not (Test-Path (Join-Path $publishDir $file))) { throw "Guardian output '$file' missing from publish dir (ProjectReference broken?)" }
}

# --- 3. Stage loose native payload (this list IS the packaging manifest) ---
# These files live in the project root and are not all produced by the publish
# (see wiki/architecture/dependency-inventory.md). Fail loudly if any is
# missing — the script is the reproducibility gate the wiki asks for.
$payload = @(
    "ADLX_3DSettings.dll", "ADLX_AutoTuning.dll", "ADLX_DisplaySettings.dll", "ADLX_PerformanceMetrics.dll",
    "BlackSharp.Core.dll", "DiskInfoToolkit.dll", "HidSharp.dll", "LibreHardwareMonitorLib.dll",
    "Magpie.dll", "MagpieRT.dll", "RAMSPDToolkit-NDD.dll", "RTSSSharedMemoryNET.dll",
    "kernel32.dll", "ucrtbased.dll", "vcruntime140d.dll", "vcruntime140_1d.dll"
)
foreach ($file in $payload) {
    $dst = Join-Path $publishDir $file
    if (-not (Test-Path $dst)) {
        $src = Join-Path $projDir $file
        if (-not (Test-Path $src)) { throw "Missing payload file '$file' (expected in project root)" }
        Copy-Item $src $dst
    }
}

# --- 4. Seed the persistent Releases dir so deltas can be built ---
dotnet tool restore | Out-Null
dotnet vpk download github --repoUrl https://github.com/jgudec/Uniwill-Control-Utility -o $releasesDir
if ($LASTEXITCODE -ne 0) {
    Write-Warning "vpk download github failed (first run? no Velopack assets on GitHub yet) — building a full-only release."
}

# --- 5. Velopack pack: per-machine MSI + full/delta nupkgs + releases.win.json ---
dotnet vpk pack -u Uniwill-Control-Utility -v $version -p $publishDir -o $releasesDir `
    -e "Uniwill Control Utility.exe" `
    --packTitle "Uniwill Control Utility" `
    --icon (Join-Path $projDir "favicon.ico") `
    --delta BestSpeed `
    -f net10-x64-desktop `
    --msi --instLocation PerMachine
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

# --- 6. Portable zip side artifact (today's distribution format) ---
$zip = Join-Path $releasesDir "Uniwill-Control-Utility-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip

# --- 7. Upload manifest ---
Write-Host "`nUpload these assets to the GitHub release tagged v$version :" -ForegroundColor Green
Get-ChildItem $releasesDir |
    Where-Object { $_.Name -match "releases\.win\.json$|full\.nupkg$|delta\.nupkg$|\.msi$|\.zip$" } |
    ForEach-Object { Write-Host "  $($_.Name)" }
