<#
.SYNOPSIS
    Publishes BladeControl and produces the release artifacts.

.DESCRIPTION
    One entry point used identically by a developer and by the release workflow, so the
    assets attached to a GitHub Release are built the same way they were built locally.

    Produces, under artifacts/:
        publish/ui, publish/service   self-contained x64 trees
        BladeControl-<v>-win-x64.msi  the installer
        BladeControl-<v>-win-x64-portable.zip
        BladeControl-<v>-win-x64-symbols.zip
        SHA256SUMS.txt

    This script never installs the MSI, never registers a service, and never touches
    hardware. Everything it does is a build step.

.PARAMETER SkipInstaller
    Publish and package the portable zip only. Useful when the WiX tool is unavailable.

.PARAMETER SkipTests
    Skip the test gate. The release workflow never passes this.
#>
[CmdletBinding()]
param(
    [switch]$SkipInstaller,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $artifacts 'publish'
$solution = Join-Path $repoRoot 'BladeControl.sln'

function Write-Step { param([string]$Message) Write-Host "`n=== $Message ===" -ForegroundColor Cyan }

# --- Version comes from Directory.Build.props and nowhere else ---------------------------
Write-Step 'Resolving product version'
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath
$versionPrefix = ($props.Project.PropertyGroup |
    Where-Object { $_.BladeControlVersionPrefix } |
    Select-Object -First 1).BladeControlVersionPrefix
$versionSuffix = ($props.Project.PropertyGroup |
    Where-Object { $null -ne $_.BladeControlVersionSuffix } |
    Select-Object -First 1).BladeControlVersionSuffix
if ([string]::IsNullOrWhiteSpace($versionPrefix)) {
    throw "Could not read BladeControlVersionPrefix from $propsPath."
}
$versionPrefix = $versionPrefix.Trim()
$fullVersion = if ([string]::IsNullOrWhiteSpace($versionSuffix)) {
    $versionPrefix
} else {
    "$versionPrefix-$($versionSuffix.Trim())"
}
Write-Host "Product version: $fullVersion (assembly $versionPrefix.0)"

# --- Clean --------------------------------------------------------------------------------
Write-Step 'Cleaning artifacts'
if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

# --- Quality gates ------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step 'Running Release tests'
    & dotnet test $solution -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; refusing to package.' }

    Write-Step 'Verifying formatting'
    & dotnet format $solution --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw 'Formatting check failed; refusing to package.' }
}

# --- Publish ------------------------------------------------------------------------------
# Self-contained so the user never has to install a .NET runtime by hand.
#
# Deliberately NOT single-file and NOT trimmed. Single-file self-extracts to a temp
# directory at startup, which is a poor fit for a LocalSystem service that must be running
# before the first sign-in, and WPF resource loading plus LibreHardwareMonitor's reflective
# sensor discovery are exactly the patterns trimming breaks. The brief is explicit that
# reliability outranks file count, and ~250 files per app inside an MSI costs the user
# nothing.
$publishArgs = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:SatelliteResourceLanguages=en',
    '--nologo'
)

Write-Step 'Publishing BladeControl.UI (self-contained x64)'
& dotnet publish (Join-Path $repoRoot 'src/BladeControl.UI/BladeControl.UI.csproj') `
    @publishArgs -o (Join-Path $publishRoot 'ui')
if ($LASTEXITCODE -ne 0) { throw 'UI publish failed.' }

Write-Step 'Publishing BladeControl.Service (self-contained x64)'
& dotnet publish (Join-Path $repoRoot 'src/BladeControl.Service/BladeControl.Service.csproj') `
    @publishArgs -o (Join-Path $publishRoot 'service')
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

# --- Guard against shipping development or test artifacts ---------------------------------
Write-Step 'Auditing publish output'
$forbidden = Get-ChildItem -Recurse -File $publishRoot | Where-Object {
    $_.Name -match '(?i)(\.Tests\.dll$|^Microsoft\.(TestPlatform|VisualStudio\.TestPlatform)|^MSTest|^testhost|\.runsettings$|^xunit|^nunit)'
}
if ($forbidden) {
    $forbidden | ForEach-Object { Write-Host "  UNEXPECTED: $($_.FullName)" -ForegroundColor Red }
    throw 'Test or development artifacts found in publish output.'
}
foreach ($required in @('ui/BladeControl.UI.exe', 'service/BladeControl.Service.exe')) {
    if (-not (Test-Path (Join-Path $publishRoot $required))) {
        throw "Publish output is missing $required."
    }
}
Write-Host ('  ui:      {0} files' -f (Get-ChildItem -File (Join-Path $publishRoot 'ui')).Count)
Write-Host ('  service: {0} files' -f (Get-ChildItem -File (Join-Path $publishRoot 'service')).Count)

# --- Separate symbols so the installer ships binaries only --------------------------------
Write-Step 'Collecting symbols'
$symbolStage = Join-Path $artifacts 'symbols'
New-Item -ItemType Directory -Force -Path $symbolStage | Out-Null
Get-ChildItem -Recurse -File -Filter '*.pdb' $publishRoot | ForEach-Object {
    $relative = $_.FullName.Substring($publishRoot.Length).TrimStart('\', '/')
    $target = Join-Path $symbolStage $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Move-Item -LiteralPath $_.FullName -Destination $target
}
$symbolsZip = Join-Path $artifacts "BladeControl-$fullVersion-win-x64-symbols.zip"
Compress-Archive -Path (Join-Path $symbolStage '*') -DestinationPath $symbolsZip -Force
Remove-Item -Recurse -Force $symbolStage

# --- Portable zip -------------------------------------------------------------------------
Write-Step 'Building portable archive'
$portableStage = Join-Path $artifacts 'portable/BladeControl'
New-Item -ItemType Directory -Force -Path $portableStage | Out-Null
Copy-Item -Recurse (Join-Path $publishRoot 'ui/*') $portableStage
New-Item -ItemType Directory -Force -Path (Join-Path $portableStage 'Runtime') | Out-Null
Copy-Item -Recurse (Join-Path $publishRoot 'service/*') (Join-Path $portableStage 'Runtime')
Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') $portableStage
Copy-Item (Join-Path $repoRoot 'docs/portable-build.md') (Join-Path $portableStage 'README-PORTABLE.md')
$portableZip = Join-Path $artifacts "BladeControl-$fullVersion-win-x64-portable.zip"
Compress-Archive -Path (Join-Path (Split-Path -Parent $portableStage) '*') `
    -DestinationPath $portableZip -Force
Remove-Item -Recurse -Force (Split-Path -Parent $portableStage)

# --- Installer ----------------------------------------------------------------------------
if (-not $SkipInstaller) {
    Write-Step 'Building MSI'
    $wixProject = Join-Path $repoRoot 'installer/BladeControl.Installer.wixproj'
    & dotnet build $wixProject -c Release "-p:PublishRoot=$publishRoot" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

    $msi = Get-ChildItem -Recurse -File -Filter '*.msi' (Join-Path $repoRoot 'installer/bin') |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $msi) { throw 'Installer build produced no .msi.' }
    Copy-Item $msi.FullName (Join-Path $artifacts $msi.Name)
    Write-Host "  $($msi.Name) ($([math]::Round($msi.Length / 1MB, 1)) MB)"
} else {
    Write-Host 'Installer skipped (-SkipInstaller).' -ForegroundColor Yellow
}

# --- Hashes -------------------------------------------------------------------------------
# Published so a user can verify a download. Pre-release builds are unsigned, which makes
# these the only integrity check available; see docs/code-signing.md.
Write-Step 'Computing SHA256 hashes'
$hashFile = Join-Path $artifacts 'SHA256SUMS.txt'
Get-ChildItem -File $artifacts | Where-Object { $_.Extension -in '.msi', '.zip' } |
    Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    } | Set-Content -LiteralPath $hashFile -Encoding ascii
Get-Content -LiteralPath $hashFile | ForEach-Object { Write-Host "  $_" }

Write-Step 'Done'
Get-ChildItem -File $artifacts | Where-Object { $_.Extension -in '.msi', '.zip', '.txt' } |
    Select-Object Name, @{ N = 'Size'; E = { '{0:N0} bytes' -f $_.Length } } | Format-Table -AutoSize
