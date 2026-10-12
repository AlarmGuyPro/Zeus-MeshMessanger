# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Step 2: fork the Zeus catalog, add the Mesh Messenger entry to registry.json,
  and push the branch ready for a pull request.

.DESCRIPTION
  Edits exactly one file, registry.json, which is what a listing pull request is
  allowed to change. It adds the feature entry and updates the catalog's
  'generated' timestamp, then validates before committing.

  It stops before opening the pull request, because that needs the description
  and the screenshots, which are yours to paste and attach.

.EXAMPLE
  pwsh ./submit-2-registry.ps1 -WorkDir "$HOME\source"
#>
[CmdletBinding()]
param(
    # Where to put the fork's clone. A 'zeus-community-features' folder is created here.
    [string] $WorkDir = "$HOME",
    [string] $Upstream = 'Zeus-SDR/zeus-community-features',
    [string] $FeatureId = 'io.github.alarmguypro.meshmessenger',
    [string] $Version = '0.4.1',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    Write-Host ''
    Write-Host "STOPPED: $Message" -ForegroundColor Red
    exit 1
}

$branch = "community/$FeatureId-$Version"

Write-Host 'Step 2 of 2: add the catalog entry' -ForegroundColor Cyan
Write-Host ''

foreach ($tool in 'gh', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Fail "$tool is not installed. See submit-1-release.ps1 for the GitHub CLI; install Git from https://git-scm.com/download/win"
    }
}
gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'The GitHub CLI is not signed in. Run: gh auth login' }

# --- the release has to exist first ----------------------------------------
$intake = "https://github.com/AlarmGuyPro/Zeus-MeshMessanger/releases/download/v$Version/$FeatureId-$Version.zip"
Write-Host "Checking the intake release is published..."
try {
    Invoke-WebRequest -Uri $intake -Method Head -MaximumRedirection 5 -ErrorAction Stop | Out-Null
    Write-Host '  Found it.' -ForegroundColor Green
}
catch {
    Fail @"
The intake package is not downloadable at

  $intake

The catalog's security scan fetches that URL, so run submit-1-release.ps1
first and let it finish.
"@
}

# --- fork and clone --------------------------------------------------------
$clone = Join-Path $WorkDir 'zeus-community-features'
if (Test-Path -LiteralPath $clone) {
    Write-Host "Using the existing clone at $clone"
    Push-Location $clone
    git fetch upstream main 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { git fetch origin main 2>&1 | Out-Null }
}
else {
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    Push-Location $WorkDir
    Write-Host "Forking $Upstream and cloning into $clone ..."
    gh repo fork $Upstream --clone 2>&1 | Write-Host
    if (-not (Test-Path -LiteralPath $clone)) { Fail 'The fork or clone did not complete. The message above says why.' }
    Pop-Location
    Push-Location $clone
}

# Whatever the source remote is called, find it rather than assuming 'upstream'.
$remotes = (git remote) -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }
$sourceRemote = if ($remotes -contains 'upstream') { 'upstream' } else { 'origin' }
Write-Host "  Source remote: $sourceRemote"

git fetch $sourceRemote main
if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "Could not fetch main from $sourceRemote." }

# --- branch ----------------------------------------------------------------
$onBranch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($onBranch -ne $branch) {
    git switch -c $branch "$sourceRemote/main" 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        git switch $branch 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "Could not create or switch to $branch." }
    }
}
Write-Host "  On branch $branch" -ForegroundColor Green

# --- the edit --------------------------------------------------------------
$registryPath = Join-Path $clone 'registry.json'
if (-not (Test-Path -LiteralPath $registryPath)) { Pop-Location; Fail 'registry.json is not in the clone.' }

$registry = Get-Content -Raw -LiteralPath $registryPath
if ($registry -match [regex]::Escape($FeatureId)) {
    Pop-Location
    Fail "registry.json already mentions $FeatureId. Nothing was changed — check whether this was already submitted."
}

# Written in the catalog's own style: two-space indent, one array element per line.
$entry = @"
    {
      "id": "$FeatureId",
      "channel": "community",
      "name": "Mesh Messenger",
      "description": "Read, send and reply to Meshtastic and MeshCore messages through Wi-Fi-connected LoRa nodes, in one inbox that keeps each network separate.",
      "author": "KQ4WLR",
      "license": "GPL-3.0-or-later",
      "homepage": "https://github.com/AlarmGuyPro/Zeus-MeshMessanger",
      "categories": [
        "tools"
      ],
      "verified": false,
      "versions": [
        {
          "version": "$Version",
          "sdkAbi": 1,
          "sdkMinVersion": "1.5.0",
          "platforms": [
            "any"
          ],
          "downloadUrl": "https://github.com/Zeus-SDR/zeus-community-features/releases/download/community-$FeatureId-v$Version/$FeatureId-$Version.zip",
          "sha256": "a854b549ce4717da09a16558cf533584771ae9c60649347de2c9b63ab28c53fc",
          "source": {
            "repository": "https://github.com/AlarmGuyPro/Zeus-MeshMessanger",
            "commit": "f4e26ca7cba194ae713a25d7b60d8fa653bcb391",
            "package": "$intake"
          }
        }
      ]
    },
"@

if ($registry -notmatch '(?m)^\s*"plugins"\s*:\s*\[\s*$') {
    Pop-Location
    Fail 'The "plugins" array in registry.json is not laid out as expected. Add the entry by hand from REGISTRY-ENTRY.json instead.'
}

$updated = [regex]::Replace(
    $registry,
    '(?m)^(\s*"plugins"\s*:\s*\[\s*)$',
    { param($m) $m.Groups[1].Value + "`n" + $entry.TrimEnd() },
    1)

$stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$updated = [regex]::Replace($updated, '"generated"\s*:\s*"[^"]*"', """generated"": ""$stamp""", 1)

if ($updated -eq $registry) { Pop-Location; Fail 'Nothing was changed — the edit did not apply. Add the entry by hand instead.' }

# Prove it is still valid JSON before writing.
try { $null = $updated | ConvertFrom-Json -Depth 100 }
catch { Pop-Location; Fail "The edit produced invalid JSON, so nothing was written: $($_.Exception.Message)" }

Set-Content -LiteralPath $registryPath -Value $updated -NoNewline -Encoding utf8
Write-Host '  registry.json updated.' -ForegroundColor Green
Write-Host "  generated set to $stamp"

# --- validate --------------------------------------------------------------
$validator = Join-Path $clone 'tools/validate-registry.ps1'
if (Test-Path -LiteralPath $validator) {
    Write-Host ''
    Write-Host 'Validating...'
    & pwsh -NoProfile -File $validator
    if ($LASTEXITCODE -ne 0) {
        Pop-Location
        Fail 'validate-registry.ps1 rejected the change. Nothing has been committed; the message above says why.'
    }
}

# --- show the diff and confirm ---------------------------------------------
Write-Host ''
git --no-pager diff --stat
# Git writes its line-ending warnings to stderr, and PowerShell folds those into
# the pipeline, so they have to be discarded or they look like changed files.
$changed = @(git --no-pager diff --name-only 2>$null | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
if ($changed.Count -ne 1 -or $changed[0] -ne 'registry.json') {
    Pop-Location
    Fail "A listing pull request may change only registry.json, but these changed: $($changed -join ', ')"
}

if (-not $Force) {
    Write-Host ''
    $answer = Read-Host 'Commit and push this branch? Type yes'
    if ($answer -ne 'yes') { Pop-Location; Write-Host 'Nothing was committed.'; exit 0 }
}

git add registry.json
git commit -m "feat(registry): add $FeatureId $Version"
if ($LASTEXITCODE -ne 0) { Pop-Location; Fail 'The commit failed.' }
git push -u origin $branch
if ($LASTEXITCODE -ne 0) { Pop-Location; Fail 'The push failed.' }

Pop-Location

Write-Host ''
Write-Host 'Branch pushed.' -ForegroundColor Green
Write-Host ''
Write-Host 'Last part, and it has to be done by hand:' -ForegroundColor Cyan
Write-Host '  1. Open the pull request:'
Write-Host "       gh pr create --repo $Upstream --base main --head $branch --title ""feat(registry): add $FeatureId $Version"""
Write-Host '  2. Paste PULL-REQUEST.md as the description.'
Write-Host '  3. Attach the 21 screenshots to the description or a review comment.'
Write-Host '     They are required review evidence, not a nicety.'
Write-Host '  4. Watch the checks:'
Write-Host "       gh pr checks --repo $Upstream --watch"
Write-Host ''
Write-Host 'The downloadUrl in the entry points at Zeus-SDR and will 404 until a' -ForegroundColor Yellow
Write-Host 'maintainer mirrors the package. That is expected, not a mistake.' -ForegroundColor Yellow
