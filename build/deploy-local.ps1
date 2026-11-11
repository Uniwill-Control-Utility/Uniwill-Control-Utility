#Requires -Version 5.1
<#
.SYNOPSIS
    Deploys a locally built UCU over the installed copy without touching the version.

.DESCRIPTION
    Test deploys must not bump <Version>: the version is a release fact, and every bump
    either creates a new ARP row or forces an uninstall/install cycle. This script does
    what `dotnet publish` + a file copy can do instead:

        publish -> stop app + engine service -> mirror the payload over the installed
        layout -> start the service -> start the app

    Settings, presets and logs live in %LOCALAPPDATA%\UCU and are never touched.

    Prerequisite: a clean install of the current version (build\pack.ps1 + the MSI) has
    been done once, so the engine service exists and points at this folder. This script
    only replaces files.

.EXAMPLE
    .\build\deploy-local.ps1
    .\build\deploy-local.ps1 -NoLaunch
#>
param(
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "Uniwill Control Utility\Uniwill Control Utility.csproj"
$publishDir = Join-Path $root "artifacts\publish"
$targetDir = "C:\Program Files\Uniwill-Control-Utility\current"
$serviceName = "UCUEngine"
$appExe = Join-Path $targetDir "Uniwill Control Utility.exe"

if (-not (Test-Path $targetDir)) {
    throw "No installed copy at '$targetDir'. Install once with build\pack.ps1 + the MSI, then use this script for test deploys."
}

$version = (dotnet msbuild $proj -getProperty:Version | Select-Object -Last 1).Trim()
Write-Host "Deploying local build (version $version, unchanged)" -ForegroundColor Cyan

# --- 1. Publish, into an empty directory -----------------------------------
# Same reasoning as pack.ps1: dotnet publish does not remove files it no longer
# produces, so a reused directory can ship a stale dll that nothing references.
if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained false -o $publishDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$publishedApp = Join-Path $publishDir "Uniwill Control Utility.exe"
$publishedEngine = Join-Path $publishDir "Service\Uniwill Control Utility Engine.exe"
if (-not (Test-Path $publishedApp)) { throw "Publish output has no app exe" }
if (-not (Test-Path $publishedEngine)) { throw "Publish output has no engine exe (Service\ subfolder)" }

# --- 2. Stop the app and the engine ----------------------------------------
Get-Process "Uniwill Control Utility" -ErrorAction SilentlyContinue | Stop-Process -Force
& sc.exe stop $serviceName | Out-Null
for ($i = 0; $i -lt 20; $i++) {
    if ((Get-Service $serviceName -ErrorAction SilentlyContinue).Status -ne "Running") { break }
    Start-Sleep -Milliseconds 250
}

# --- 3. Mirror the payload over the install folder -------------------------
# /E copies subdirectories including empty ones; no /MIR: a delete pass over an
# install folder is not something a deploy script should be allowed to do.
& robocopy $publishDir $targetDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

# --- 4. Restart the service, then the app ----------------------------------
& sc.exe start $serviceName | Out-Null
for ($i = 0; $i -lt 40; $i++) {
    if ((Get-Service $serviceName -ErrorAction SilentlyContinue).Status -eq "Running") { break }
    Start-Sleep -Milliseconds 250
}
$state = (Get-Service $serviceName -ErrorAction SilentlyContinue).Status
if ($state -ne "Running") { throw "The engine service did not start (state: $state)" }

$deployed = (Get-Item $appExe).VersionInfo.FileVersion
Write-Host "Deployed: app $deployed, engine service $state" -ForegroundColor Green

if (-not $NoLaunch) {
    Start-Process $appExe
    Write-Host "App launched."
}
