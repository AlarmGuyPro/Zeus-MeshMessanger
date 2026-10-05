# SPDX-License-Identifier: GPL-2.0-or-later
# Builds the feature ZIP from zeus-build.json, the same contract the Zeus
# catalog's rebuild check uses, so the local package and the CI rebuild are
# assembled from one list of files.
#
# Why not the template's build-package.ps1: it copies UI modules from inside
# the .NET project folder, but the rebuild check requires the browser build to
# live outside it (web/). Path-safety rules below mirror the template's.
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string] $Configuration = "Release",
    [switch] $SkipWeb
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Assert-Inside([string] $Parent, [string] $Candidate, [string] $Label) {
    $rel = [IO.Path]::GetRelativePath($Parent, $Candidate).Replace("\", "/")
    if ($rel -eq "." -or $rel -eq ".." -or $rel.StartsWith("../") -or [IO.Path]::IsPathRooted($rel)) {
        throw "$Label escapes $Parent : $Candidate"
    }
}

function Assert-SafeRelative([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains("\") -or $Path.StartsWith("/") -or
        $Path.EndsWith("/") -or $Path.Contains(":")) {
        throw "Unsafe path in zeus-build.json: $Path"
    }
    foreach ($segment in $Path.Split("/")) {
        if ($segment -in @("", ".", "..") -or $segment -match '[<>"|?*\x00-\x1F]' -or $segment -match '[. ]$') {
            throw "Unsafe path segment in zeus-build.json: $Path"
        }
    }
}

function Assert-NoLink([string] $Path, [string] $Label) {
    $current = $repoRoot
    foreach ($segment in [IO.Path]::GetRelativePath($repoRoot, $Path).Replace("\", "/").Split("/")) {
        $current = Join-Path $current $segment
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "$Label passes through a filesystem link: $current"
        }
    }
}

$contract = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "zeus-build.json") | ConvertFrom-Json -Depth 20
$projectPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $contract.dotnet.project))
$projectDir = Split-Path -Parent $projectPath
$manifestSource = $contract.package."plugin.json"
if (-not $manifestSource) { throw "zeus-build.json must map plugin.json" }
$manifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot $manifestSource) | ConvertFrom-Json -Depth 50
$id = [string]$manifest.id
$version = [string]$manifest.version
$entrypoint = [string]$manifest.entrypoint.assembly

# 1. Browser builds, in contract order (the rebuild check runs them first too).
if (-not $SkipWeb) {
    foreach ($node in @($contract.node)) {
        Assert-SafeRelative $node.directory
        Push-Location (Join-Path $repoRoot $node.directory)
        try {
            npm ci --ignore-scripts
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed in $($node.directory)" }
            npm run $node.script --ignore-scripts
            if ($LASTEXITCODE -ne 0) { throw "npm run $($node.script) failed in $($node.directory)" }
        }
        finally { Pop-Location }
    }
}

# 2. .NET build.
dotnet build $projectPath -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $LASTEXITCODE" }
$outDir = Join-Path $projectDir "bin/$Configuration/net10.0"

# 3. Stage: every DLL the build produced (the entrypoint plus our own libraries)
#    and the entrypoint .deps.json from build output; everything else from the
#    contract map. The Zeus contracts DLL is provided by the host and must not ship.
$artifactRoot = Join-Path $repoRoot "artifacts/$id"
$staging = Join-Path $artifactRoot "staging"
if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$outputFiles = @(Get-ChildItem -LiteralPath $outDir -Filter *.dll -File |
    Where-Object { $_.Name -ne "Zeus.Plugins.Contracts.dll" } |
    ForEach-Object { $_.Name })
if ($outputFiles -notcontains $entrypoint) { throw "Build output missing entrypoint: $entrypoint" }
$outputFiles += [IO.Path]::ChangeExtension($entrypoint, ".deps.json")
foreach ($file in $outputFiles) {
    $src = Join-Path $outDir $file
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw "Build output missing: $src" }
    Assert-NoLink $src "Build output"
    Copy-Item -LiteralPath $src -Destination (Join-Path $staging $file)
}

foreach ($entry in $contract.package.PSObject.Properties) {
    $zipPath = [string]$entry.Name
    $srcRel = [string]$entry.Value
    Assert-SafeRelative $zipPath
    Assert-SafeRelative $srcRel
    if ($zipPath -like "*.dll" -or $zipPath -like "*.deps.json") {
        throw "DLLs and .deps.json come from the build output; do not map them: $zipPath"
    }
    $src = [IO.Path]::GetFullPath((Join-Path $repoRoot $srcRel))
    Assert-Inside $repoRoot $src "Package source"
    Assert-NoLink $src "Package source"
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw "Package source not found: $srcRel" }
    $dest = [IO.Path]::GetFullPath((Join-Path $staging $zipPath))
    Assert-Inside $staging $dest "Package destination"
    if (Test-Path -LiteralPath $dest) { throw "Duplicate package path: $zipPath" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
    Copy-Item -LiteralPath $src -Destination $dest
}

# 4. Zip + checksum.
$package = Join-Path $artifactRoot "$id-$version.zip"
if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $package, [IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -LiteralPath $staging -Recurse -Force

$sha = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$package.sha256" -Value "$sha  $([IO.Path]::GetFileName($package))" -Encoding ascii
Write-Host "Package: $package"
Write-Host "SHA-256: $sha"
