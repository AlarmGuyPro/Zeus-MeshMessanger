# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Step 2: fork the Zeus catalog, add the Mesh Messenger 1.0.0 version to the
  existing entry in registry.json, and push the branch ready for a pull request.

.DESCRIPTION
  Mesh Messenger is already listed: pull request #19 was merged, so registry.json
  carries the entry with version 0.4.1. This adds 1.0.0 as another element of that
  entry's 'versions' array and leaves the published 0.4.1 object untouched — the
  catalog's custody test rejects any edit to a version it has already published.

  Edits exactly one file, registry.json, which is what a listing pull request is
  allowed to change, and validates before committing.

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
    [string] $Version = '1.0.0',
    [string] $Commit = '0bda3574bb39071b868d1922ba68a3ff2fdcf7e6',
    [string] $Sha256 = 'ea697ccf2e404a2f1fb2bf6a8ab3f98e047016bf144361cb4ec545b2ea1771ea',
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

# --- branch, from the current upstream main --------------------------------
# 0.4.1 is already merged there, so the branch must start from it rather than
# from an older local main.
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

# Normalised to LF. Git hands Windows a CRLF working copy, which would stop the
# insertion below from finding its anchor. Git normalises again on commit, so
# writing LF back keeps the diff clean.
$registry = (Get-Content -Raw -LiteralPath $registryPath) -replace "`r`n", "`n"

if ($registry -notmatch [regex]::Escape($FeatureId)) {
    Pop-Location
    Fail "registry.json does not mention $FeatureId at all. Expected the merged 0.4.1 entry — is this clone up to date with $Upstream?"
}
if ($registry -match [regex]::Escape("`"version`": `"$Version`"") -and
    $registry -match [regex]::Escape($FeatureId)) {
    # A crude but sufficient check: the version string appears somewhere already.
    $entryStart = $registry.IndexOf($FeatureId)
    $entryEnd = $registry.IndexOf('"id":', $entryStart)
    if ($entryEnd -lt 0) { $entryEnd = $registry.Length }
    if ($registry.Substring($entryStart, $entryEnd - $entryStart) -match [regex]::Escape("`"version`": `"$Version`"")) {
        Pop-Location
        Fail "registry.json already lists $FeatureId $Version. Nothing was changed."
    }
}

# The anchor is the end of this feature's own 'versions' array: the last
# source.package line belonging to it, then the three closing lines. Anchoring on
# the feature's package URL keeps the edit inside the right entry.
$anchor = @"
            "package": "https://github.com/AlarmGuyPro/Zeus-MeshMessanger/releases/download/v0.4.1/$FeatureId-0.4.1.zip"
          }
        }
      ]
"@ -replace "`r`n", "`n"

if (([regex]::Matches($registry, [regex]::Escape($anchor))).Count -ne 1) {
    Pop-Location
    Fail @"
Could not find the end of this feature's versions array in registry.json.

Add the new version by hand instead, from REGISTRY-ENTRY.json: append it to the
'versions' array of the io.github.alarmguypro.meshmessenger entry, after the
0.4.1 object, and change nothing else. registry-1.0.0.patch shows the exact edit.
"@
}

$addition = @"
            "package": "https://github.com/AlarmGuyPro/Zeus-MeshMessanger/releases/download/v0.4.1/$FeatureId-0.4.1.zip"
          }
        },
        {
          "version": "$Version",
          "sdkAbi": 1,
          "sdkMinVersion": "1.5.0",
          "platforms": [
            "any"
          ],
          "downloadUrl": "https://github.com/Zeus-SDR/zeus-community-features/releases/download/community-$FeatureId-v$Version/$FeatureId-$Version.zip",
          "sha256": "$Sha256",
          "source": {
            "repository": "https://github.com/AlarmGuyPro/Zeus-MeshMessanger",
            "commit": "$Commit",
            "package": "$intake"
          }
        }
      ]
"@ -replace "`r`n", "`n"

$updated = $registry.Replace($anchor, $addition)

$stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$updated = [regex]::Replace($updated, '"generated"\s*:\s*"[^"]*"', """generated"": ""$stamp""", 1)

if ($updated -eq $registry) { Pop-Location; Fail 'Nothing was changed — the edit did not apply. Add the version by hand instead.' }

# Prove it is still valid JSON, and that 0.4.1 is untouched, before writing.
try { $parsed = $updated | ConvertFrom-Json -Depth 100 }
catch { Pop-Location; Fail "The edit produced invalid JSON, so nothing was written: $($_.Exception.Message)" }

$entry = @($parsed.plugins | Where-Object { $_.id -eq $FeatureId })
if ($entry.Count -ne 1) { Pop-Location; Fail "Expected exactly one $FeatureId entry after the edit, found $($entry.Count)." }
$listed = @($entry[0].versions | ForEach-Object { [string]$_.version })
if ($listed -notcontains '0.4.1') { Pop-Location; Fail 'The published 0.4.1 version disappeared. Nothing was written.' }
if ($listed -notcontains $Version) { Pop-Location; Fail "$Version was not added. Nothing was written." }
Write-Host "  Versions for this feature after the edit: $($listed -join ', ')" -ForegroundColor Green

Set-Content -LiteralPath $registryPath -Value $updated -NoNewline -Encoding utf8
Write-Host '  registry.json updated.' -ForegroundColor Green
Write-Host "  generated set to $stamp"

# --- validate --------------------------------------------------------------
foreach ($script in 'tools/validate-registry.ps1', 'tools/test-community-custody.ps1') {
    $validator = Join-Path $clone $script
    if (-not (Test-Path -LiteralPath $validator)) { continue }
    Write-Host ''
    Write-Host "Running $script ..."
    & pwsh -NoProfile -File $validator
    if ($LASTEXITCODE -ne 0) {
        Pop-Location
        Fail "$script rejected the change. Nothing has been committed; the message above says why."
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
Write-Host '  3. Attach the screenshots to the description or a review comment.'
Write-Host '     They are required review evidence, not a nicety.'
Write-Host '  4. Watch the checks:'
Write-Host "       gh pr checks --repo $Upstream --watch"
Write-Host ''
Write-Host 'The downloadUrl in the entry points at Zeus-SDR and will 404 until a' -ForegroundColor Yellow
Write-Host 'maintainer mirrors the package. That is expected, not a mistake.' -ForegroundColor Yellow
