# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Step 2: fork the Zeus catalog, add the Mesh Messenger 1.0.3 version to the
  existing entry in registry.json, and push the branch ready for a pull request.

.DESCRIPTION
  Mesh Messenger is already listed: pull request #19 was merged, so registry.json
  carries the entry with version 0.4.1. This adds 1.0.3 as another element of that
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
    [string] $Version = '1.0.3',
    [string] $Commit = '8e21064a7f5154ced78006302e0052b9a1ff331d',
    [string] $Sha256 = '09a3389462776798f46a995a23f0f79bfbfbb17eeef6d7f8a2beb909a77b68aa',
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

# A re-run has to start from the catalog's own registry.json rather than from the
# copy a previous attempt already edited, whether that attempt committed or not.
# The branch belongs to this script, so when everything on it is this script's own
# commit it is rewound; when the file is merely dirty it is restored. Anything else
# on the branch is left alone, and the check below explains itself.
$rewound = $false
$head = "$(git rev-parse HEAD 2>$null | Select-Object -First 1)".Trim()
$sourceHead = "$(git rev-parse "$sourceRemote/main" 2>$null | Select-Object -First 1)".Trim()
$commitMessage = "feat(registry): add $FeatureId $Version"

if ($head -and $sourceHead -and $head -ne $sourceHead) {
    $subjects = @(git log --format=%s "$sourceRemote/main..HEAD" 2>$null | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $foreign = @($subjects | Where-Object { $_ -cne $commitMessage })
    # Nothing but registry.json may be dirty, so a rewind cannot discard other work.
    $dirty = @(git status --porcelain 2>$null | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $dirtyElsewhere = @($dirty | Where-Object { $_ -notmatch 'registry\.json$' })
    if ($subjects.Count -gt 0 -and $foreign.Count -eq 0 -and $dirtyElsewhere.Count -eq 0) {
        git reset --hard "$sourceRemote/main" 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "Could not rewind $branch to $sourceRemote/main." }
        $rewound = $true
        Write-Host "  Rewound $branch to $sourceRemote/main — it carried only this script's own commit." -ForegroundColor Yellow
    }
}
elseif ($head -and $sourceHead) {
    git restore --source "$sourceRemote/main" -- registry.json 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) { Pop-Location; Fail 'Could not restore registry.json from the source remote.' }
    Write-Host "  registry.json restored from $sourceRemote/main before editing."
}

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
        Fail @"
registry.json already lists $FeatureId $Version, and this branch already carries a
commit, so the edit was not repeated and nothing was changed.

If that commit is the one you want, it only needs pushing:

  git -C "$clone" push -u origin $branch

To start the branch over instead, delete it and run this script again:

  git -C "$clone" switch --detach $sourceRemote/main
  git -C "$clone" branch -D $branch
"@
    }
}

# The new version goes at the FRONT of this feature's 'versions' array, and that is
# a rule rather than a preference: tools/validate-community-submission.ps1 throws
# "A release PR must prepend exactly one new immutable version" unless
# versions[0].version is the new one. Appending it after 0.4.1 passes every local
# gate — including the custody test, which only ever uses your registry.json as the
# BASE of its own fixture submission and so never checks this — and then fails the
# "Trusted community submission policy" check on the pull request, which is the only
# place the comparison runs in the direction that matters.
#
# So the insertion point is the line after this feature's '"versions": [', found by
# starting from the feature's own id so it cannot land in another entry.
$idPos = $registry.IndexOf("`"id`": `"$FeatureId`"", [StringComparison]::Ordinal)
$versionsPos = if ($idPos -ge 0) { $registry.IndexOf("`"versions`": [", $idPos, [StringComparison]::Ordinal) } else { -1 }
$lineEnd = if ($versionsPos -ge 0) { $registry.IndexOf("`n", $versionsPos) } else { -1 }

if ($lineEnd -lt 0) {
    Pop-Location
    Fail @"
Could not find the start of this feature's versions array in registry.json.

Add the new version by hand instead, from REGISTRY-ENTRY.json: insert it as the
FIRST element of the 'versions' array of the io.github.alarmguypro.meshmessenger
entry, before the 0.4.1 object, and change nothing else. registry-$Version.patch
shows the exact edit.
"@
}

$addition = @"
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
        },

"@ -replace "`r`n", "`n"

# The here-string ends with a newline, so the 0.4.1 object that follows keeps its
# own line. $lineEnd + 1 is the character after the newline of '"versions": ['.
$updated = $registry.Insert($lineEnd + 1, $addition)

# Two minutes back, on purpose, and the reason is the custody test rather than
# taste. tools/test-community-custody.ps1 builds its own fixture submission on top
# of whatever registry.json is in the tree and stamps that fixture with UtcNow to
# the second, then asserts the fixture's timestamp is STRICTLY LATER than the one it
# built on. A registry.json stamped with UtcNow moments earlier ties on the second —
# or wins, if the clock moves — and the test fails with "A listing PR must advance
# the generated timestamp", which reads like a complaint about this edit and is not.
# Anything from today still advances the catalog's own timestamp, which is what the
# real check compares against, so the two minutes cost nothing.
$stamp = [DateTime]::UtcNow.AddMinutes(-2).ToString('yyyy-MM-ddTHH:mm:ssZ')
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
if ($listed[0] -cne $Version) {
    Pop-Location
    Fail "The new version has to be first in the versions array, and after the edit the order is $($listed -join ', '). Nothing was written."
}
Write-Host "  Versions for this feature after the edit: $($listed -join ', ')" -ForegroundColor Green

Set-Content -LiteralPath $registryPath -Value $updated -NoNewline -Encoding utf8
Write-Host '  registry.json updated.' -ForegroundColor Green
Write-Host "  generated set to $stamp"

# --- validate --------------------------------------------------------------
# First the check the pull request will actually run, in the direction it runs it:
# main's registry.json as the base, ours as the candidate. This is the one the
# "Trusted community submission policy" job performs, and nothing else here covers
# it — validate-registry only reads our file, and the custody test uses our file as
# the base of its own fixture. A wrong version order passes both and fails this.
$submissionCheck = Join-Path $clone 'tools/validate-community-submission.ps1'
if (Test-Path -LiteralPath $submissionCheck) {
    Write-Host ''
    Write-Host 'Running tools/validate-community-submission.ps1 against the catalog main ...'
    $baseCopy = Join-Path ([IO.Path]::GetTempPath()) "zeus-base-registry-$([Guid]::NewGuid().ToString('N')).json"

    # git's stdout is redirected to the file by the operating system rather than read
    # through PowerShell. A pipeline would decode those bytes using the console's code
    # page, and registry.json holds em dashes and middle dots in six other features'
    # descriptions: on a Windows console they come back mangled, the check then sees
    # seven changed features instead of one, and it fails with "A listing PR must add
    # exactly one feature or one version without changing anything else" — a complaint
    # about six entries this edit never touched.
    $git = Start-Process -FilePath 'git' -WorkingDirectory $clone -Wait -PassThru -NoNewWindow `
        -ArgumentList @('show', "${sourceRemote}/main:registry.json") `
        -RedirectStandardOutput $baseCopy

    # And the copy has to be provably the same blob before anything is concluded from
    # comparing against it. git hashes the file we just wrote and the blob it came
    # from; if those differ, this one check is skipped rather than trusted.
    $writtenHash = if ($git.ExitCode -eq 0 -and (Test-Path -LiteralPath $baseCopy)) { "$(git hash-object $baseCopy)".Trim() } else { '' }
    $blobHash = "$(git rev-parse "${sourceRemote}/main:registry.json")".Trim()

    if ($writtenHash -and $blobHash -and $writtenHash -eq $blobHash) {
        & pwsh -NoProfile -File $submissionCheck `
            -BaseRegistryPath $baseCopy `
            -CandidateRegistryPath $registryPath `
            -ExpectedFeatureId $FeatureId `
            -ExpectedVersion $Version
        $submissionExit = $LASTEXITCODE
        Remove-Item -LiteralPath $baseCopy -ErrorAction SilentlyContinue
        if ($submissionExit -ne 0) {
            Pop-Location
            Fail 'The catalog submission policy rejected the change. Nothing has been committed; the message above says why.'
        }
    }
    else {
        Remove-Item -LiteralPath $baseCopy -ErrorAction SilentlyContinue
        Write-Host '  SKIPPED: could not reproduce main''s registry.json byte for byte here,' -ForegroundColor Yellow
        Write-Host '  so this check would be comparing against something other than main.' -ForegroundColor Yellow
        Write-Host '  The pull request runs it either way; the gates below still apply.' -ForegroundColor Yellow
    }
}

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
git commit -m $commitMessage
if ($LASTEXITCODE -ne 0) { Pop-Location; Fail 'The commit failed.' }
# A rewound branch needs the remote's copy replaced rather than added to, and
# --force-with-lease refuses if anything arrived on it that this clone has not seen.
if ($rewound) { git push --force-with-lease -u origin $branch }
else { git push -u origin $branch }
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
