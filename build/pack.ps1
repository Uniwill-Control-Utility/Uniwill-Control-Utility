#Requires -Version 5.1
<#
.SYNOPSIS
    Packages Uniwill Control Utility for release: publish, stage native payload,
    Velopack pack (per-machine MSI + full/delta nupkgs).

.DESCRIPTION
    Produces the GitHub release assets for Uniwill-Control-Utility/Uniwill-Control-Utility:
      Releases\Uniwill-Control-Utility.msi            per-machine installer (Program Files)
      Releases\Uniwill-Control-Utility-<ver>-full.nupkg   Velopack full package
      Releases\Uniwill-Control-Utility-<ver>-delta.nupkg  Velopack delta from the previous release
      Releases\releases.win.json                      Velopack GitHub feed index

    No portable zip is produced: the engine runs as a machine-wide service
    created on first launch, so an unpacked copy has no update or removal
    story of its own (deleting the folder leaves the service registered).

    The version is read from the csproj (single source of truth) - bump it there,
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

# --- 2a. Clean the publish layout ---
# The publish dir is reused across runs, and `dotnet publish` only writes the files its
# projects produce: an assembly from a *deleted* project stays in the layout forever, and
# the MSI/ZIP templates ship every file in the directory. That is how the pre-engine service
# ("Uniwill Control Utility Service.*", deleted with the engine migration) kept riding into
# every payload afterwards - privileged code no service entry refers to. Publish into an
# empty directory so the payload is exactly what the current projects produce.
if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }

# --- 2b. Publish (framework-dependent, NOT single-file) ---
# PublishSingleFile=false is mandatory: native DLLs (ADLX, Magpie, kernel32,
# RTSS) are P/Invoked from the exe directory and break under single-file
# extraction. The MSI ensures the .NET 10 Desktop Runtime via -f below.
dotnet publish $proj -c Release -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# --- 2c. Verify the engine service flowed into the layout ---
# The engine ships in the Service\ subfolder (csproj staging target: the
# engine project is built/published with its output redirected there), so the
# install root shows a single app exe and the service's full dependency
# closure (Core/Contracts/Hardware, hosting, Serilog) is self-contained. The
# app ensures the "UCUEngine" service at startup.
foreach ($file in @(
    "Service\Uniwill Control Utility Engine.exe", "Service\Uniwill Control Utility Engine.dll",
    "Service\Uniwill Control Utility Engine.deps.json", "Service\Uniwill Control Utility Engine.runtimeconfig.json",
    "Service\UCU.Core.dll", "Service\UCU.Contracts.dll", "Service\UCU.Hardware.dll",
    "Service\Microsoft.Extensions.Hosting.dll", "Service\Serilog.Extensions.Hosting.dll"
)) {
    if (-not (Test-Path (Join-Path $publishDir $file))) { throw "Service payload '$file' missing from publish dir (staging target broken?)" }
}

# --- 2c'. Freshness: the staged engine must come from this build ---
# Presence is not freshness. The staging target runs the engine's Build/Publish
# with its output redirected into Service\, and that copy step is the one place a
# stale service binary can ride into both the MSI and the portable ZIP: before the
# target got its own IntermediateOutputPath, MSBuild's copy markers made it skip
# the copy, so Service\ kept the previous binary while the app build stayed green.
# The publish dir is written by this run, so an engine file older than the app exe
# means exactly that skip. The version check catches the other drift: Version
# lives in Directory.Build.props for every project, so a mismatched engine file
# version can only be a stale payload.
$appExe = Get-Item (Join-Path $publishDir "Uniwill Control Utility.exe")
foreach ($engineFile in @(
    "Service\Uniwill Control Utility Engine.dll",
    "Service\Uniwill Control Utility Engine.exe"
)) {
    $engine = Get-Item (Join-Path $publishDir $engineFile)
    if ($engine.LastWriteTime -lt $appExe.LastWriteTime) {
        throw "Stale engine payload: '$engineFile' ($($engine.LastWriteTime)) is older than the app exe ($($appExe.LastWriteTime)) - the staging target skipped its copy"
    }
    if ($engine.VersionInfo.FileVersion -notlike "$version*") {
        throw "Engine payload version drift: '$engineFile' file version '$($engine.VersionInfo.FileVersion)' does not match release version $version"
    }
}

# --- 2c''. No retired assembly may ride into the payload ---
# Belt to 2a, so the invariant is pinned rather than assumed: the base names below are the
# service/bridge assemblies the project layout has retired. Any of them in the layout means
# stale files (or a revived name), and the installer would ship privileged code that nothing
# starts. Matched on base name so .dll/.exe/.pdb/.deps.json/.runtimeconfig.json all count.
$retiredBaseNames = @(
    "Uniwill Control Utility Service", "Uniwill Control Utility Guardian", "Uniwill Control Utility Bridge",
    "UCU.Service", "UCU.Guardian", "UCU.Bridge"
)
$leaked = @(
    Get-ChildItem $publishDir -Recurse -File |
        Where-Object { $retiredBaseNames -contains [IO.Path]::GetFileNameWithoutExtension($_.Name) } |
        ForEach-Object { $_.FullName.Substring($publishDir.Length + 1) }
)
if ($leaked.Count -gt 0) {
    throw "Retired service assemblies in the publish payload: $($leaked -join ', ') - the layout is stale (step 2a) or a retired name came back"
}

# --- 2d. Compile the Uninstall.exe shim into the layout ---
# The MSI template ships every file in the publish dir, so a shim compiled
# here lands next to the app in MSI installs and portable zips. Built with
# the always-present .NET Framework csc - no toolchain dependency.
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
# /target:winexe (not exe): the shim is a GUI app ([STAThread] + MessageBox);
# a console-subsystem build makes Explorer allocate a console window for it
# that sits empty through the whole uninstall.
& $csc /nologo /target:winexe /platform:x64 `
    "/win32icon:$(Join-Path $projDir 'favicon.ico')" `
    "/win32manifest:$(Join-Path $PSScriptRoot 'uninstall-shim\admin.manifest')" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/out:$(Join-Path $publishDir 'Uninstall.exe')" `
    (Join-Path $PSScriptRoot "uninstall-shim\UninstallShim.cs")
if ($LASTEXITCODE -ne 0) { throw "Uninstall.exe shim compile failed" }

# --- 2e. Compile the UCU.Updater.exe shim (elevated update apply) ---
# The per-machine layout needs admin to write, and the running engine service
# locks the Service\ payload the update replaces. The shim (requireAdministrator,
# same manifest as Uninstall.exe) stops the engine, runs Uninstall.exe -a
# --from-update, and starts the engine again; the app routes its Velopack apply
# through it when it exists. Same framework csc as above, no dependencies.
& $csc /nologo /target:winexe /platform:x64 `
    "/win32icon:$(Join-Path $projDir 'favicon.ico')" `
    "/win32manifest:$(Join-Path $PSScriptRoot 'uninstall-shim\admin.manifest')" `
    "/out:$(Join-Path $publishDir 'UCU.Updater.exe')" `
    (Join-Path $PSScriptRoot "updater-shim\UpdaterShim.cs")
if ($LASTEXITCODE -ne 0) { throw "UCU.Updater.exe shim compile failed" }

# --- 3. Stage loose native payload (this list IS the packaging manifest) ---
# These files live in the project root and are not all produced by the publish
# (see wiki/architecture/dependency-inventory.md). Fail loudly if any is
# missing - the script is the reproducibility gate the wiki asks for.
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
dotnet vpk download github --repoUrl https://github.com/Uniwill-Control-Utility/Uniwill-Control-Utility -o $releasesDir
if ($LASTEXITCODE -ne 0) {
    Write-Warning "vpk download github failed (first run? no Velopack assets on GitHub yet) - building a full-only release."
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

# vpk always emits its own portable zip (there is no --noPortable in 1.2.161).
# We do not ship one: the engine runs as a machine-wide service created on first
# launch, so an unpacked copy has no update or removal story of its own.
$vpkPortable = Join-Path $releasesDir "Uniwill-Control-Utility-win-Portable.zip"
if (Test-Path $vpkPortable) {
    Remove-Item $vpkPortable -Force
    Write-Host "Removed vpk's portable zip (not a shipped asset)" -ForegroundColor DarkGray
}

# --- 5a. Rebind the MSI's Add/Remove Programs entry to Uninstall.exe ------
# The Control Panel / Settings uninstall must run the complete-sweep shim,
# not a thin `msiexec /x`. The packed vpk template already publishes ONE ARP
# entry itself (ARPSYSTEMCOMPONENT=1 suppresses the Windows Installer engine's
# GUID-keyed entry - the one carrying WindowsInstaller=1, i.e. the entry the
# Control Panel routes by product code straight into msiexec), under
#   HKLM\...\Uninstall\MSI:Uniwill-Control-Utility
# with UninstallString "msiexec.exe /x [ProductCode]": the entry is
# component-clean (installed and deleted with `ArpRegistration`), but the
# command is thin - the engine service is registered by the app, not the MSI,
# so the removal leaves service, layout, tasks and entry behind.
#
# This step authors the rebind INTO the package: UninstallString and
# QuietUninstallString become "[INSTALLFOLDER]Uninstall.exe" (the
# requireAdministrator shim: app kill, service stop+delete, tasks, MSI /x,
# ARP entries, install root). The key is non-GUID and carries no
# WindowsInstaller value, so appwiz.cpl/Settings shell-execute the string
# verbatim - the one Control Panel entry the product has IS the complete
# route. Values ship inside the signed-source MSI: no runtime registry
# writer, so no AV anti-hijack enforcer class applies (the runtime
# re-assertion in EngineInstaller.RepairControlPanelUninstall stays as the
# belt for dev layouts and hand-installed packages). The sentinel + evidence
# floor remain the net for scripted `msiexec /x` routes: with the engine GUID
# entry suppressed there is no GUID-keyed registration to watch, so the
# sentinel's probe unions the ARP view with MsiEnumProductsW.
#
# Exe-CustomActions that launch the SHIM at uninstall stay a field-proven
# dead end in this package (verbose-log forensics in the git history: every
# encoding tried at the time failed 1721/2715/2723/2727 or a CreateProcess
# failure under the msiserver session) - the ARP rebinding needs no CA at
# all. (Step 5c later found the working exe-CA encoding - type 18 File
# source + deferred + NoImpersonate - for the engine verbs; the shim route
# remains unproven and is NOT what 5c authors.)
#
# msi-edit is compiled on demand: the IDatabase interface is vtable-only
# (no late-bound route), so the step needs a TlbImp interop of msi.dll;
# both tools live in the SDK, and the artifacts are cached under obj\.
$msiEditDir = Join-Path $PSScriptRoot "msi-edit\obj"
New-Item -ItemType Directory -Path $msiEditDir -Force | Out-Null
$wiInterop = Join-Path $msiEditDir "WindowsInstaller.Interop.dll"
if (-not (Test-Path $wiInterop)) {
    $tlbimp = Get-ChildItem "C:\Program Files (x86)\Microsoft SDKs\Windows\*\bin\NETFX*\x64\TlbImp.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tlbimp) { $tlbimp = Get-Command tlbimp.exe -ErrorAction SilentlyContinue }
    if (-not $tlbimp) { throw "TlbImp.exe not found (Windows SDK): cannot rebind the MSI's ARP entry" }
    $tiPath = if ($tlbimp.FullName) { $tlbimp.FullName } else { $tlbimp.Source }
    & $tiPath "$env:WINDIR\System32\msi.dll" "/out:$wiInterop" "/namespace:WindowsInstaller" /silent
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $wiInterop)) { throw "TlbImp interop of msi.dll failed" }
}
$msiEditSrc = Join-Path $PSScriptRoot "msi-edit\MsiEdit.cs"
$msiEdit = Join-Path $msiEditDir "msi-edit.exe"
# Rebuild when the exe is missing OR stale (source edited after the last
# compile: a cached older tool silently packs an older policy).
if (-not (Test-Path $msiEdit) -or
    (Get-Item $msiEditSrc).LastWriteTime -gt (Get-Item $msiEdit).LastWriteTime) {
    & $csc /nologo /target:exe "/out:$msiEdit" "/reference:$wiInterop" $msiEditSrc
    if ($LASTEXITCODE -ne 0) { throw "msi-edit compile failed" }
}
$packedMsi = Get-ChildItem $releasesDir -Filter "*.msi" | Select-Object -First 1
if (-not $packedMsi) { throw "no packed MSI found in $releasesDir" }
& $msiEdit suppress $packedMsi.FullName
if ($LASTEXITCODE -ne 0) { throw "msi-edit suppress failed: ARP entry not bound to the shim" }
$arpEcho = & $msiEdit inspect $packedMsi.FullName 2>&1 | Out-String
if ($arpEcho -notmatch "UninstallString\s*\|\s*""\[INSTALLFOLDER\]Uninstall\.exe""") {
    throw "post-edit verification: no ARP row rebound to the shim visible in the MSI"
}
# The published key must carry the documented MSI:{product-code} name: the
# product-code route appwiz takes when an enforcer re-adds WindowsInstaller=1
# resolves the name as the code (a non-code name yields the "may have already
# been uninstalled" dialog - twice field-proven).
$arpCode = if ($arpEcho -match "ProductCode\s*=\s*(\{[A-F0-9-]+\})") { $Matches[1] } else { $null }
if (-not $arpCode) { throw "post-edit verification: product code not readable from the packed MSI" }
if ($arpEcho -notmatch [regex]::Escape("Uninstall\MSI:$arpCode")) {
    throw "post-edit verification: published ARP key is not named MSI:$arpCode"
}
Write-Host "Rebound $($packedMsi.Name): the single Control Panel entry now runs Uninstall.exe" -ForegroundColor Cyan

# --- 5a. U-111 (MSI registers the engine service) is a proven dead end ----
# The goal was real: a fresh install paid one UAC because the app's
# unelevated chain registered UCUEngine. The mechanism Windows Installer
# prescribes - ServiceInstall/ServiceControl/MsiLockPermissionsEx rows +
# InstallServices/StartServices/StopServices/DeleteServices - cannot be
# authored into THIS package on current Windows, field-probed 2026-10-31
# (every probe against a copy of the packed MSI):
#   - CREATE TABLE parses nowhere: COM OpenView, the C API
#     (MsiDatabaseOpenViewW), transacted, direct, read-write: 1615
#     ERROR_BAD_QUERY on every syntax, including the SDK's documented
#     examples. msi.dll's SQL on this OS has no DDL.
#   - MsiDatabaseImport of an .idt: 1627 (imports rows into existing
#     tables only). MsiDatabaseMerge: hr=0 no-op - the source database
#     cannot create the tables either.
#   - The compound-file route (writing table streams as light.exe does):
#     the OS's own APIs refuse it - StgCreateStorageEx INVALIDFUNCTION,
#     StgOpenStorageEx ABNORMALAPIWRITE on every format, transacted
#     CreateStream/OpenStream INVALIDFUNCTION, on a freshly packed MSI,
#     and the control docfile created moments earlier by the same API.
#     This is the OS guard against non-MSI writers; routing around it is
#     not on the table.
# The engine is registered at INSTALL time by U-111b (step 5c below): a
# deferred exe CustomAction running the engine's own --install-engine verb
# as SYSTEM - pure DML on the existing CustomAction/InstallExecuteSequence
# tables, no service tables, no DDL. The documented, elevated app-side
# `--install-engine` pass REMAINS as the self-heal route (dev layouts,
# hand-installed packages, Velopack update moves the exe). msi-edit's
# `export` mode keeps MSI's ISTG format inspectable if a future Windows
# gives msi.dll its DDL back.

# --- 5c. U-111b: author the engine-service CustomActions -----------------
# Fresh installs pay ZERO UAC at first launch: the MSI's deferred
# CustomActions (SYSTEM, NoImpersonate) run --install-engine inside the
# install, --remove-service inside the uninstall. Encoding, sequencing,
# and the --remove-service-only rule: documented in msi-edit
# AddServiceCustomActions; field-proven 2026-10-31 (the 4099-row cycle ran
# full /i->/x->/i->/x exit 0 and forced rollback fired the 3346 row
# cleanly, but the belt ran BEFORE the ARP registry rows committed - the
# Control Panel regression - so the install CA moved to 6000, after the
# registry batch and RegisterProduct, and the sdset convergence budget to
# twenty attempts to outlast the SCM create-commit restore).
& $msiEdit addca $packedMsi.FullName
if ($LASTEXITCODE -ne 0) { throw "msi-edit addca failed: engine service CustomActions not authored" }
$caEcho = & $msiEdit inspect $packedMsi.FullName 2>&1 | Out-String
foreach ($want in @('UcuEngineInstallDeferred\s*\|\s*3090', 'UcuEngineInstallRollback\s*\|\s*3346', 'UcuEngineRemoveDeferred\s*\|\s*3090', 'UcuCloseApp\s*\|\s*114', 'UcuStopEngine\s*\|\s*114')) {
    if ($caEcho -notmatch $want) { throw "post-edit verification: CustomAction row missing in the packed MSI ($want)" }
}

if ($caEcho -notmatch "--uninstall-engine") {
    # Belt: the sweep verb must never appear as a CA target inside the MSI.
    Write-Host "Engine service CustomActions authored (install @6000 + rollback @5999, remove @3498, engine verbs only)" -ForegroundColor Cyan
} else {
    throw "post-edit verification: the sweep verb --uninstall-engine appears in a CA target (must be --remove-service)"
}

# --- 5b. Stamp the Setup.exe bootstrapper requireAdministrator ---
# vpk's bootstrapper template is asInvoker and vpk exposes no manifest flag,
# so a double-click on a per-machine pack fails mid-install with
# ERROR_ELEVATION_REQUIRED and leaves a partial layout in %LOCALAPPDATA%.
# Inject build\setup-elevation.manifest (resource #1) with the SDK's mt.exe.
$setupExe = Get-ChildItem $releasesDir -Filter "*-Setup.exe" | Select-Object -First 1
if ($setupExe) {
    # SDK bin dirs are versioned (10.0.26100.0\x64) plus an unversioned x64\
    # fallback; pick the newest versioned dir, fall back to any hit.
    $mtCandidates = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\mt.exe" -ErrorAction SilentlyContinue
    $mt = $mtCandidates | Where-Object { $_.Directory.Name -match '^\d+(\.\d+){3}$' } |
        Sort-Object { [version]$_.Directory.Name } | Select-Object -Last 1
    if (-not $mt) { $mt = $mtCandidates | Select-Object -First 1 }
    if (-not $mt) {
        Write-Warning "mt.exe not found in the Windows SDK - Setup.exe stays asInvoker; run it elevated."
    } else {
        # The fresh Setup.exe is transiently locked by real-time AV scanning
        # (field-proven: mt c101008d "cannot open the device or file" right
        # after vpk writes it). A short bounded retry rides out the scan;
        # persistent failure stays loud.
        $stamped = $false
        for ($try = 1; $try -le 5; $try++) {
            & $mt.FullName -manifest (Join-Path $PSScriptRoot "setup-elevation.manifest") `
                "-outputresource:$($setupExe.FullName);#1"
            if ($LASTEXITCODE -eq 0) { $stamped = $true; break }
            Start-Sleep -Seconds (5 * $try)
        }
        if (-not $stamped) { throw "mt.exe failed to stamp $($setupExe.Name)" }
        Write-Host "Stamped $($setupExe.Name) requireAdministrator" -ForegroundColor Cyan
    }
}

# --- 6. Upload manifest ---
Write-Host "`nUpload these assets to the GitHub release tagged v$version :" -ForegroundColor Green
Get-ChildItem $releasesDir |
    Where-Object { $_.Name -match "releases\.win\.json$|Uniwill-Control-Utility-$([regex]::Escape($version))-(full|delta)\.nupkg$|\.msi$|win-Setup\.exe$" } |
    ForEach-Object { Write-Host "  $($_.Name)" }
