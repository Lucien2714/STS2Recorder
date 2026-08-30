<#
.SYNOPSIS
    Reports whether the vendored STS2MCP copies have gone stale.

.DESCRIPTION
    Compares vendor/UPSTREAM.json against the STS2MCP repo and reports three
    kinds of divergence:

      stale    - upstream has moved on; the vendored files differ from the
                 current upstream contents. Re-run sync-upstream.ps1 to adopt.
      edited   - a vendored copy was modified locally, so it no longer matches
                 the upstream commit it claims to come from. This is the
                 dangerous one: the recorder's output silently stops matching
                 STS2MCP's.
      behind   - upstream commit differs but file contents are identical
                 (upstream changed files we don't vendor). Harmless.

    Exit code is 0 unless -Strict is passed, so this never blocks a build; the
    build calls it for a warning only.

.PARAMETER Source
    Path to the STS2MCP repo. Same resolution order as sync-upstream.ps1.

.PARAMETER Strict
    Exit non-zero when anything is stale or edited. For CI.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [switch]$Strict
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$vendorDir   = Join-Path $repoRoot 'vendor/sts2mcp'
$manifestOut = Join-Path $repoRoot 'vendor/UPSTREAM.json'

if (-not (Test-Path $manifestOut)) {
    Write-Warning "vendor/UPSTREAM.json missing - run scripts/sync-upstream.ps1."
    if ($Strict) { exit 1 }
    exit 0
}

$manifest = Get-Content -Raw $manifestOut | ConvertFrom-Json

if (-not $Source) { $Source = $env:STS2MCP_DIR }
if (-not $Source) { $Source = Join-Path (Split-Path -Parent $repoRoot) 'STS2MCP' }

if (-not (Test-Path (Join-Path $Source '.git'))) {
    Write-Host "STS2MCP not available at '$Source'; skipping drift check." -ForegroundColor DarkGray
    Write-Host "Vendored from commit $($manifest.commit)." -ForegroundColor DarkGray
    exit 0
}

Push-Location $Source
try { $upstreamCommit = (git rev-parse HEAD).Trim() } finally { Pop-Location }

$stale  = @()
$edited = @()

foreach ($f in $manifest.files) {
    $upstreamPath = Join-Path $Source $f.path
    $vendoredPath = Join-Path $vendorDir (Split-Path -Leaf $f.path)

    # The vendored copy carries a prepended do-not-edit header, so compare it to
    # the recorded hash by stripping the header rather than hashing the file.
    if (Test-Path $vendoredPath) {
        $text = Get-Content -Raw -Encoding UTF8 $vendoredPath
        $marker = "// ---{0}" -f ('-' * 74)
        $idx = $text.LastIndexOf('// -----------------------------------------------------------------------------')
        if ($idx -ge 0) {
            $body = $text.Substring($idx)
            $body = $body.Substring($body.IndexOf("`n") + 1).TrimStart("`r", "`n")
        } else {
            $body = $text
        }
        $sha = [BitConverter]::ToString(
            [System.Security.Cryptography.SHA256]::HashData(
                [System.Text.Encoding]::UTF8.GetBytes($body))
        ).Replace('-', '').ToLowerInvariant()

        if ($sha -ne $f.sha256) { $edited += $f.path }
    } else {
        $edited += "$($f.path) (missing)"
    }

    if (Test-Path $upstreamPath) {
        $upstreamSha = (Get-FileHash -Algorithm SHA256 -Path $upstreamPath).Hash.ToLowerInvariant()
        if ($upstreamSha -ne $f.sha256) { $stale += $f.path }
    }
}

if ($edited) {
    Write-Warning "Vendored files were EDITED locally - recorder output no longer matches STS2MCP:"
    $edited | ForEach-Object { Write-Warning "    $_" }
    Write-Warning "Fix upstream in STS2MCP instead, then: .\scripts\sync-upstream.ps1"
}

if ($stale) {
    Write-Warning "Vendored files are STALE (upstream has changed):"
    $stale | ForEach-Object { Write-Warning "    $_" }
    Write-Warning "Adopt upstream changes with: .\scripts\sync-upstream.ps1"
}

if (-not $edited -and -not $stale) {
    if ($upstreamCommit -ne $manifest.commit) {
        Write-Host "Vendored files up to date (upstream moved to $($upstreamCommit.Substring(0,7)), but no vendored file changed)." -ForegroundColor DarkGray
    } else {
        Write-Host "Vendored files up to date with STS2MCP @ $($manifest.commit.Substring(0,7))." -ForegroundColor DarkGray
    }
}

if ($Strict -and ($edited -or $stale)) { exit 1 }
exit 0
