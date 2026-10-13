# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Step 1 of submitting Mesh Messenger to the Zeus community catalog: publish the
  tested ZIP as a GitHub Release on your own repository.

.DESCRIPTION
  The catalog downloads the package from a versioned release on your repo and
  checks its SHA-256 before reading a byte of it, so this script refuses to
  publish anything whose hash is not the exact one in the registry entry.

  Nothing is uploaded until you confirm.

.EXAMPLE
  pwsh ./submit-1-release.ps1 -ZipPath "$HOME\Downloads\io.github.alarmguypro.meshmessenger-1.0.0.zip"
#>
[CmdletBinding()]
param(
    # The ZIP to publish. Use the one from chat, or builds/v1.0.0/ — never a fresh rebuild.
    [Parameter(Mandatory)][string] $ZipPath,
    [string] $Repo = 'AlarmGuyPro/Zeus-MeshMessanger',
    [string] $Tag = 'v1.0.0',
    [string] $Commit = '0bda3574bb39071b868d1922ba68a3ff2fdcf7e6',
    [string] $ExpectedHash = 'ea697ccf2e404a2f1fb2bf6a8ab3f98e047016bf144361cb4ec545b2ea1771ea',
    # Skip the confirmation prompt.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    Write-Host ''
    Write-Host "STOPPED: $Message" -ForegroundColor Red
    exit 1
}

Write-Host 'Step 1 of 2: publish the intake release' -ForegroundColor Cyan
Write-Host ''

# --- the GitHub CLI --------------------------------------------------------
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail @'
The GitHub CLI is not installed. Install it, then run this again:

    winget install --id GitHub.cli

Close and reopen PowerShell afterwards so the new command is on your PATH.
'@
}

gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Fail @'
The GitHub CLI is installed but not signed in. Run:

    gh auth login

Choose GitHub.com, HTTPS, and "Login with a web browser". Then run this again.
'@
}
Write-Host '  GitHub CLI ready.' -ForegroundColor Green

# --- the archive -----------------------------------------------------------
if (-not (Test-Path -LiteralPath $ZipPath)) { Fail "No file at $ZipPath" }
$zip = (Resolve-Path -LiteralPath $ZipPath).Path
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()

Write-Host "  File:     $zip"
Write-Host "  SHA-256:  $actual"

if ($actual -ne $ExpectedHash) {
    Fail @"
That is not the archive the catalog entry describes.

  expected  $ExpectedHash
  found     $actual

This is almost always because the packaging script was run again: a rebuild
produces a working but byte-different assembly, so the hash changes. Use the
archive that was delivered in chat, or the copy on the repository's 'builds'
branch under v1.0.0/. Do not rebuild it.
"@
}
Write-Host '  Hash matches the catalog entry.' -ForegroundColor Green

# --- does the tag already exist? -------------------------------------------
$existing = gh release view $Tag --repo $Repo 2>$null
if ($LASTEXITCODE -eq 0) {
    Fail @"
A release tagged $Tag already exists on $Repo.

Published bytes must never be replaced. If the package has changed, build a new
version (1.0.1), get a new hash, and submit that instead.
"@
}

# --- confirm ---------------------------------------------------------------
Write-Host ''
Write-Host 'About to create:' -ForegroundColor Yellow
Write-Host "  repository  $Repo"
Write-Host "  tag         $Tag  (pointing at commit $($Commit.Substring(0,7)))"
Write-Host "  asset       $(Split-Path -Leaf $zip)"
Write-Host ''
Write-Host 'Once published, these bytes can never be changed at this URL.' -ForegroundColor Yellow

if (-not $Force) {
    $answer = Read-Host 'Type yes to publish'
    if ($answer -ne 'yes') { Write-Host 'Nothing was published.'; exit 0 }
}

gh release create $Tag $zip `
    --repo $Repo `
    --target $Commit `
    --title 'Mesh Messenger 1.0.0' `
    --notes 'Intake release for the Zeus community catalog. Built from 0bda3574bb39071b868d1922ba68a3ff2fdcf7e6.'
if ($LASTEXITCODE -ne 0) { Fail 'gh release create failed. The message above says why.' }

$url = "https://github.com/$Repo/releases/download/$Tag/$(Split-Path -Leaf $zip)"
Write-Host ''
Write-Host 'Published.' -ForegroundColor Green
Write-Host "  $url"
Write-Host ''
Write-Host 'Checking the download URL is live...'
try {
    $head = Invoke-WebRequest -Uri $url -Method Head -MaximumRedirection 5 -ErrorAction Stop
    Write-Host "  Reachable (HTTP $($head.StatusCode))." -ForegroundColor Green
}
catch {
    Write-Host '  Not reachable yet. GitHub can take a moment; check the link in a browser.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Next: run submit-2-registry.ps1' -ForegroundColor Cyan
