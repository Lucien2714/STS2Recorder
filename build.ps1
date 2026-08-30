<#
.SYNOPSIS
    Builds the STS2_Recorder mod DLL.

.DESCRIPTION
    Compiles STS2_Recorder.dll against the game's assemblies. Does NOT install
    the mod unless -Install is passed.

.PARAMETER GameDir
    Path to the Slay the Spire 2 installation directory. Falls back to the
    STS2_GAME_DIR environment variable, then to Directory.Build.props.

.PARAMETER Sts2McpDir
    Path to the STS2MCP repo, used only for the vendored-source drift check.
    Falls back to STS2MCP_DIR, then to a sibling directory.

.PARAMETER Configuration
    Build configuration (default: Release).

.PARAMETER Install
    Copy the built DLL and manifest into <GameDir>\mods\.

.EXAMPLE
    .\build.ps1 -GameDir "E:\SteamLibrary\steamapps\common\Slay the Spire 2"
    .\build.ps1 -Install
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Sts2McpDir,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Install
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot

# --- Resolve the game directory ------------------------------------------------

if (-not $GameDir) { $GameDir = $env:STS2_GAME_DIR }
if (-not $GameDir -and (Test-Path (Join-Path $scriptDir 'Directory.Build.props'))) {
    $props = Get-Content -Raw (Join-Path $scriptDir 'Directory.Build.props')
    $m = [regex]::Match($props, '<STS2GameDir>([^<]+)</STS2GameDir>')
    if ($m.Success) { $GameDir = $m.Groups[1].Value.Trim() }
}

if (-not $GameDir) {
    Write-Host @"
ERROR: Game directory not specified.

Provide it via parameter, environment variable, or Directory.Build.props:
  .\build.ps1 -GameDir "E:\SteamLibrary\steamapps\common\Slay the Spire 2"
  `$env:STS2_GAME_DIR = "E:\SteamLibrary\steamapps\common\Slay the Spire 2"
  Copy-Item Directory.Build.props.example Directory.Build.props   # then edit
"@ -ForegroundColor Red
    exit 1
}

$dllDir = Join-Path $GameDir 'data_sts2_windows_x86_64'
if (-not (Test-Path (Join-Path $dllDir 'sts2.dll'))) {
    Write-Host "ERROR: Could not find sts2.dll in '$dllDir'." -ForegroundColor Red
    Write-Host "Make sure -GameDir points to the Slay the Spire 2 installation root." -ForegroundColor Red
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: 'dotnet' not found. Install the .NET 9 SDK:" -ForegroundColor Red
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/9.0" -ForegroundColor Red
    exit 1
}

# --- Sanity-check the vendored sources ----------------------------------------

if (-not (Test-Path (Join-Path $scriptDir 'vendor/UPSTREAM.json'))) {
    Write-Host @"
ERROR: vendor/UPSTREAM.json is missing, so there is nothing to build against.

Populate the vendored STS2MCP sources first:
  .\scripts\sync-upstream.ps1 -Source <path to STS2MCP>
"@ -ForegroundColor Red
    exit 1
}

# --- Build ---------------------------------------------------------------------

$project = Join-Path $scriptDir 'STS2_Recorder.csproj'
$outDir  = Join-Path $scriptDir 'out/STS2_Recorder'

Write-Host "=== Building STS2_Recorder ($Configuration) ===" -ForegroundColor Cyan
Write-Host "Game directory : $GameDir"
Write-Host "Output         : $outDir"
Write-Host ""

$buildArgs = @($project, '-c', $Configuration, '-o', $outDir, "-p:STS2GameDir=$GameDir")
if ($Sts2McpDir) { $buildArgs += "-p:Sts2McpDir=$Sts2McpDir" }

dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "=== Build succeeded ===" -ForegroundColor Green

# --- Install -------------------------------------------------------------------

if ($Install) {
    $modsDir = Join-Path $GameDir 'mods'
    New-Item -ItemType Directory -Force -Path $modsDir | Out-Null

    Copy-Item (Join-Path $outDir 'STS2_Recorder.dll') $modsDir -Force
    Copy-Item (Join-Path $scriptDir 'mod_manifest.json') (Join-Path $modsDir 'STS2_Recorder.json') -Force

    Write-Host "Installed to $modsDir" -ForegroundColor Green
} else {
    Write-Host "To install, copy these files to <game_install>\mods\:"
    Write-Host "  $outDir\STS2_Recorder.dll"
    Write-Host "  $scriptDir\mod_manifest.json  ->  mods\STS2_Recorder.json"
    Write-Host ""
    Write-Host "Or re-run with -Install."
}
