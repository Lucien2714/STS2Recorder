<#
.SYNOPSIS
    Re-copies the vendored STS2MCP source files into vendor/sts2mcp/.

.DESCRIPTION
    STS2Recorder compiles a handful of STS2MCP's source files verbatim so that
    its state serialization is byte-for-byte the same code as STS2MCP's
    GET /api/v1/singleplayer response. Those files are vendored (plain copies)
    rather than pulled in via a submodule, so this script is what keeps the copy
    honest:

      1. copies every file listed in scripts/vendor-manifest.txt,
      2. records the upstream repo, commit, branch and per-file SHA-256 in
         vendor/UPSTREAM.json.

    That manifest is what lets check-drift.ps1 answer "is my copy stale?" and
    what gets stamped into every recording as state_builder_commit.

.PARAMETER Source
    Path to the STS2MCP repo. Falls back to the STS2MCP_DIR environment
    variable, then to a sibling directory next to this repo.

.PARAMETER AllowDirty
    Sync even if the upstream working tree has uncommitted changes. The recorded
    commit is then suffixed '-dirty' and provenance is only approximate.

.EXAMPLE
    .\scripts\sync-upstream.ps1
    .\scripts\sync-upstream.ps1 -Source E:\personal\dev\STS2MCP
#>
[CmdletBinding()]
param(
    [string]$Source,
    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$vendorDir   = Join-Path $repoRoot 'vendor/sts2mcp'
$manifestOut = Join-Path $repoRoot 'vendor/UPSTREAM.json'
$fileList    = Join-Path $PSScriptRoot 'vendor-manifest.txt'

# --- Resolve the upstream repo -------------------------------------------------

if (-not $Source) { $Source = $env:STS2MCP_DIR }
if (-not $Source) { $Source = Join-Path (Split-Path -Parent $repoRoot) 'STS2MCP' }

if (-not (Test-Path (Join-Path $Source '.git'))) {
    throw @"
STS2MCP repo not found at '$Source'.

Pass it explicitly or set the environment variable:
  .\scripts\sync-upstream.ps1 -Source <path to STS2MCP>
  `$env:STS2MCP_DIR = '<path to STS2MCP>'
"@
}
$Source = (Resolve-Path $Source).Path

# --- Read the file list --------------------------------------------------------

$files = Get-Content $fileList |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') }

if (-not $files) { throw "No files listed in $fileList." }

# --- Capture upstream provenance ----------------------------------------------

Push-Location $Source
try {
    $commit = (git rev-parse HEAD).Trim()
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    $origin = (git remote get-url origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $origin) { $origin = '<no origin remote>' }
    $origin = $origin.Trim()

    # Only the files we actually copy need to be committed. Unrelated churn
    # elsewhere in STS2MCP (IDE-generated csproj edits, WIP on other files)
    # does not make the recorded commit a lie about *these* files.
    $dirtyVendored = @(git status --porcelain -- $files) | Where-Object { $_ }
} finally {
    Pop-Location
}

if ($dirtyVendored) {
    if (-not $AllowDirty) {
        throw @"
These vendored files have uncommitted changes upstream in '$Source':

$($dirtyVendored -join "`n")

Recordings stamp the upstream commit as provenance, and copying uncommitted
work makes that stamp unresolvable. Commit them upstream, or re-run with
-AllowDirty to accept an approximate '<sha>-dirty' stamp.
"@
    }
    Write-Warning "Vendored files have uncommitted upstream changes; recording provenance as '$commit-dirty'."
    $commit = "$commit-dirty"
}

New-Item -ItemType Directory -Force -Path $vendorDir | Out-Null

$header = @'
// -----------------------------------------------------------------------------
//  VENDORED FILE -- DO NOT EDIT.
//
//  Copied verbatim from STS2MCP by scripts/sync-upstream.ps1. Local edits are
//  silently overwritten on the next sync and break the guarantee that this
//  recorder's state output matches STS2MCP's API output.
//
//  Fix bugs upstream in STS2MCP, then re-run:  .\scripts\sync-upstream.ps1
//  Provenance for this copy lives in vendor/UPSTREAM.json.
// -----------------------------------------------------------------------------

'@

$records = foreach ($rel in $files) {
    $src = Join-Path $Source $rel
    if (-not (Test-Path $src)) { throw "Listed file not found upstream: $rel" }

    $dst = Join-Path $vendorDir (Split-Path -Leaf $rel)

    # Prepend the do-not-edit header, keeping the upstream bytes otherwise
    # intact. The hash recorded below is of the ORIGINAL upstream file, so
    # check-drift.ps1 can compare against upstream without allowing for it.
    $body = Get-Content -Raw -Encoding UTF8 $src
    Set-Content -Path $dst -Value ($header + $body) -Encoding UTF8 -NoNewline

    $sha = (Get-FileHash -Algorithm SHA256 -Path $src).Hash.ToLowerInvariant()
    Write-Host ("  {0,-32} {1}" -f (Split-Path -Leaf $rel), $sha.Substring(0, 12))

    [ordered]@{ path = $rel; sha256 = $sha }
}

# --- Record provenance ---------------------------------------------------------

$manifest = [ordered]@{
    '_comment'  = 'Generated by scripts/sync-upstream.ps1. Do not edit by hand.'
    repo        = $origin
    branch      = $branch
    commit      = $commit
    synced_at   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    files       = @($records)
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestOut -Encoding UTF8

Write-Host ""
Write-Host "Vendored $($files.Count) file(s) from $Source" -ForegroundColor Green
Write-Host "  commit : $commit ($branch)"
Write-Host "  record : vendor/UPSTREAM.json"
