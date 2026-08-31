<#
.SYNOPSIS
    Builds the STS2_Recorder mod DLL.

.DESCRIPTION
    Compiles STS2_Recorder.dll against the game's assemblies. Does NOT install
    the mod unless -Install is passed.

.PARAMETER GameDir
    Path to the Slay the Spire 2 installation directory. Falls back to the
    STS2_GAME_DIR environment variable, then to Directory.Build.props.

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

# --- Sanity-check the vendored STS2MCP sources --------------------------------

if (-not (Test-Path (Join-Path $scriptDir 'vendor/STS2MCP/McpMod.StateBuilder.cs'))) {
    Write-Host @"
ERROR: vendor/STS2MCP/McpMod.StateBuilder.cs is missing, so the state serializer
this mod compiles is not there. It is vendored in this repo, not fetched - see
vendor/README.md.
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

dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "=== Build succeeded ===" -ForegroundColor Green

# --- Install -------------------------------------------------------------------

# The game loads a mod from its own folder under mods/, alongside a
# mod_manifest.json - not from a loose DLL in mods/ itself. Installing to the
# wrong shape is silent: the game keeps running whatever is in the folder, so a
# fix can look installed and never load. STS2_Recorder.conf lives here too and
# is the player's, so it is never touched.
if ($Install) {
    $modDir = Join-Path $GameDir 'mods/STS2Recorder'
    New-Item -ItemType Directory -Force -Path $modDir | Out-Null

    $dll = Join-Path $outDir 'STS2_Recorder.dll'

    try {
        Copy-Item $dll (Join-Path $modDir 'STS2_Recorder.dll') -Force -ErrorAction Stop
    }
    catch {
        Write-Host "ERROR: could not replace the installed DLL. Close Slay the Spire 2 and re-run." -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }

    Copy-Item (Join-Path $scriptDir 'mod_manifest.json') (Join-Path $modDir 'mod_manifest.json') -Force

    $installed = Get-Item (Join-Path $modDir 'STS2_Recorder.dll')
    Write-Host "Installed to $modDir" -ForegroundColor Green
    Write-Host ("  STS2_Recorder.dll  {0:yyyy-MM-dd HH:mm:ss}  {1:N0} bytes" -f $installed.LastWriteTime, $installed.Length)
} else {
    Write-Host "To install, copy these into <game_install>\mods\STS2Recorder\:"
    Write-Host "  $outDir\STS2_Recorder.dll"
    Write-Host "  $scriptDir\mod_manifest.json"
    Write-Host ""
    Write-Host "Or re-run with -Install."
}
