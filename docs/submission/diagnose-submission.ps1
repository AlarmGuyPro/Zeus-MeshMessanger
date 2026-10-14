# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Explains why tools/validate-community-submission.ps1 rejected a listing edit.

.DESCRIPTION
  The catalog's policy check compares the registry.json on the catalog's main
  against the one in your working tree and insists on exactly one changed feature
  or exactly one new feature. When neither holds it throws

    A listing PR must add exactly one feature or one version without changing
    anything else

  which says that the comparison came out some other way without saying how. This
  prints what the comparison actually sees: how many features each side has, which
  ids are new or missing, which existing features it considers changed, and for each
  of those, which properties differ.

  It reads only. Nothing is written to the clone, nothing is committed.

.EXAMPLE
  pwsh ./diagnose-submission.ps1
#>
[CmdletBinding()]
param(
    [string] $WorkDir = "$HOME",
    [string] $FeatureId = 'io.github.alarmguypro.meshmessenger',
    [string] $Version = '1.0.3'
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    Write-Host ''
    Write-Host "STOPPED: $Message" -ForegroundColor Red
    exit 1
}

# Same canonical form the catalog's own check uses: properties sorted by name, all
# the way down, then compact JSON compared case-sensitively.
function ConvertTo-CanonicalValue($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [pscustomobject]) {
        $ordered = [ordered]@{}
        foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) {
            $ordered[$property.Name] = ConvertTo-CanonicalValue $property.Value
        }
        return [pscustomobject]$ordered
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        $converted = @($Value | ForEach-Object { ConvertTo-CanonicalValue $_ })
        Write-Output -NoEnumerate $converted
        return
    }
    return $Value
}

function ConvertTo-CanonicalJson($Value) {
    return (ConvertTo-CanonicalValue $Value | ConvertTo-Json -Depth 100 -Compress)
}

$clone = Join-Path $WorkDir 'zeus-community-features'
if (-not (Test-Path -LiteralPath $clone)) { Fail "There is no clone at $clone." }
Push-Location $clone

try {
    Write-Host 'Environment' -ForegroundColor Cyan
    Write-Host "  PowerShell      $($PSVersionTable.PSVersion) on $($PSVersionTable.Platform ?? 'Win32NT')"
    Write-Host "  Clone           $clone"

    $remotes = (git remote) -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $sourceRemote = if ($remotes -contains 'upstream') { 'upstream' } else { 'origin' }
    Write-Host "  Source remote   $sourceRemote"
    Write-Host "  Branch          $((git rev-parse --abbrev-ref HEAD).Trim())"
    Write-Host "  HEAD            $((git log --oneline -1 HEAD) -join '')"
    Write-Host "  $sourceRemote/main   $((git log --oneline -1 "$sourceRemote/main") -join '')"

    # git writes the blob straight to the file. Read through a PowerShell pipeline
    # instead and the bytes are decoded with the console's code page, which mangles
    # the em dashes and middle dots in six features' descriptions and makes those
    # features look changed.
    $baseCopy = Join-Path ([IO.Path]::GetTempPath()) "zeus-diagnose-base-$([Guid]::NewGuid().ToString('N')).json"
    $git = Start-Process -FilePath 'git' -WorkingDirectory $clone -Wait -PassThru -NoNewWindow `
        -ArgumentList @('show', "${sourceRemote}/main:registry.json") `
        -RedirectStandardOutput $baseCopy
    if ($git.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $baseCopy)) { Fail "Could not read registry.json from $sourceRemote/main." }
    $writtenHash = "$(git hash-object $baseCopy)".Trim()
    $blobHash = "$(git rev-parse "${sourceRemote}/main:registry.json")".Trim()
    Write-Host ''
    Write-Host 'Base copy integrity' -ForegroundColor Cyan
    Write-Host "  blob   $blobHash"
    Write-Host "  file   $writtenHash"
    if ($writtenHash -cne $blobHash) {
        Write-Host '  The copy is NOT the same blob, so everything below compares against' -ForegroundColor Red
        Write-Host '  something other than main. That is the bug, not the registry edit.' -ForegroundColor Red
    }

    $candidatePath = Join-Path $clone 'registry.json'
    $baseBytes = [IO.File]::ReadAllBytes($baseCopy)
    $candidateBytes = [IO.File]::ReadAllBytes($candidatePath)
    Write-Host ''
    Write-Host 'Files being compared' -ForegroundColor Cyan
    Write-Host "  base        $baseCopy"
    Write-Host "              $($baseBytes.Length) bytes, BOM: $($baseBytes.Length -ge 3 -and $baseBytes[0] -eq 0xEF -and $baseBytes[1] -eq 0xBB -and $baseBytes[2] -eq 0xBF)"
    Write-Host "  candidate   $candidatePath"
    Write-Host "              $($candidateBytes.Length) bytes, BOM: $($candidateBytes.Length -ge 3 -and $candidateBytes[0] -eq 0xEF -and $candidateBytes[1] -eq 0xBB -and $candidateBytes[2] -eq 0xBF)"

    $base = Get-Content -Raw -LiteralPath $baseCopy | ConvertFrom-Json -Depth 100
    $candidate = Get-Content -Raw -LiteralPath $candidatePath | ConvertFrom-Json -Depth 100

    Write-Host ''
    Write-Host 'Top level' -ForegroundColor Cyan
    Write-Host "  schemaVersion   base $($base.schemaVersion)   candidate $($candidate.schemaVersion)"
    Write-Host "  generated       base $($base.generated)   candidate $($candidate.generated)"
    Write-Host "  generated type  base $($base.generated.GetType().Name)   candidate $($candidate.generated.GetType().Name)"
    Write-Host "  plugins         base $(@($base.plugins).Count)   candidate $(@($candidate.plugins).Count)"

    $baseIds = @(@($base.plugins) | ForEach-Object { [string]$_.id })
    $candidateIds = @(@($candidate.plugins) | ForEach-Object { [string]$_.id })
    $missing = @($baseIds | Where-Object { $_ -cnotin $candidateIds })
    $added = @($candidateIds | Where-Object { $_ -cnotin $baseIds })

    Write-Host ''
    Write-Host 'Identity' -ForegroundColor Cyan
    Write-Host "  new ids in the candidate: $(if ($added.Count) { $added -join ', ' } else { 'none' })"
    Write-Host "  ids missing from it:      $(if ($missing.Count) { $missing -join ', ' } else { 'none' })"
    $duplicates = @($candidateIds | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    Write-Host "  duplicated ids:           $(if ($duplicates.Count) { $duplicates -join ', ' } else { 'none' })"

    # Which features the check would call changed, and why.
    $changed = [Collections.Generic.List[string]]::new()
    foreach ($id in $baseIds) {
        $before = @(@($base.plugins) | Where-Object { [string]$_.id -ceq $id })[0]
        $after = @(@($candidate.plugins) | Where-Object { [string]$_.id -ceq $id })[0]
        if ($null -eq $after) { continue }
        if ((ConvertTo-CanonicalJson $before) -cne (ConvertTo-CanonicalJson $after)) { $changed.Add($id) }
    }

    Write-Host ''
    Write-Host "Features the check considers changed: $($changed.Count)" -ForegroundColor Cyan
    if ($changed.Count -eq 0) { Write-Host '  none' }

    foreach ($id in $changed) {
        $before = @(@($base.plugins) | Where-Object { [string]$_.id -ceq $id })[0]
        $after = @(@($candidate.plugins) | Where-Object { [string]$_.id -ceq $id })[0]
        Write-Host ''
        Write-Host "  $id" -ForegroundColor Yellow
        $names = @(@($before.PSObject.Properties.Name) + @($after.PSObject.Properties.Name) | Sort-Object -Unique)
        foreach ($name in $names) {
            $b = if ($before.PSObject.Properties.Name -ccontains $name) { ConvertTo-CanonicalJson $before.$name } else { '<absent>' }
            $a = if ($after.PSObject.Properties.Name -ccontains $name) { ConvertTo-CanonicalJson $after.$name } else { '<absent>' }
            if ($b -cne $a) {
                Write-Host "    $name differs" -ForegroundColor Yellow
                if ($name -ceq 'versions') {
                    Write-Host "      base versions:      $(@($before.versions | ForEach-Object { [string]$_.version }) -join ', ')"
                    Write-Host "      candidate versions: $(@($after.versions | ForEach-Object { [string]$_.version }) -join ', ')"
                    foreach ($v in @($before.versions | ForEach-Object { [string]$_.version })) {
                        $vb = @($before.versions | Where-Object { [string]$_.version -ceq $v })[0]
                        $va = @($after.versions | Where-Object { [string]$_.version -ceq $v })[0]
                        if ($null -eq $va) { Write-Host "      published $v is GONE from the candidate" -ForegroundColor Red; continue }
                        if ((ConvertTo-CanonicalJson $vb) -cne (ConvertTo-CanonicalJson $va)) {
                            Write-Host "      published $v was EDITED:" -ForegroundColor Red
                            Write-Host "        base      $(ConvertTo-CanonicalJson $vb)"
                            Write-Host "        candidate $(ConvertTo-CanonicalJson $va)"
                        }
                    }
                }
                else {
                    # Metadata, which a listing edit may not touch at all.
                    Write-Host "      base      $b"
                    Write-Host "      candidate $a"
                }
            }
        }
    }

    Write-Host ''
    Write-Host 'What the check wants' -ForegroundColor Cyan
    Write-Host "  exactly one new feature with no changed features, or no new features"
    Write-Host "  and exactly one changed feature whose versions[0] is the new version."
    Write-Host "  For this submission: no new features, $FeatureId changed, and"
    Write-Host "  its versions[0] must be $Version."
    $entry = @(@($candidate.plugins) | Where-Object { [string]$_.id -ceq $FeatureId })
    if ($entry.Count -eq 1) {
        Write-Host "  Candidate order for $FeatureId : $(@($entry[0].versions | ForEach-Object { [string]$_.version }) -join ', ')"
    }

    Remove-Item -LiteralPath $baseCopy -ErrorAction SilentlyContinue
}
finally {
    Pop-Location
}
