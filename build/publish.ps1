<#
.SYNOPSIS
  Builds the self-contained win-x64 release zip: dist\MusicFinder-<version>-win-x64.zip

.DESCRIPTION
  Runs the tests first (skip with -SkipTests), publishes the app with the .NET runtime inside (no install needed),
  adds portable.flag, THIRD-PARTY-NOTICES.md and a README-FIRST.txt, then zips the folder and prints a SHA-256.
  Nothing outside dist\ is touched. No signing is done here (see docs\release-checklist.md).
#>
param(
    [switch]$SkipTests,
    [switch]$NoPortable
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

$dist = Join-Path $repo 'dist'
$name = "MusicFinder-$version-win-x64"
$folder = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

if (-not $SkipTests) {
    dotnet test -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }
}

# Only ever delete inside dist\.
foreach ($path in @($folder, $zip)) {
    if (Test-Path $path) {
        if (-not ((Resolve-Path $path).Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase))) { throw "Refusing to delete outside dist: $path" }
        Remove-Item $path -Recurse -Force
    }
}

dotnet publish src\MusicFinder.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:PublishTrimmed=false `
    -p:DebugType=none -p:DebugSymbols=false -o $folder --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $folder
(Get-Content (Join-Path $PSScriptRoot 'README-FIRST.txt') -Raw).Replace('{VERSION}', $version) |
    Set-Content (Join-Path $folder 'README-FIRST.txt') -Encoding ascii
if (-not $NoPortable) { Set-Content (Join-Path $folder 'portable.flag') 'Remove this file to keep data in %LOCALAPPDATA%\MusicFinder.' -Encoding ascii }

# A published folder must not contain anything personal.
$bad = Get-ChildItem $folder -Recurse -Include *.db,*.db-wal,*.db-shm,settings.json,*.log -ErrorAction SilentlyContinue
if ($bad) { throw "Unexpected user data in the package: $($bad.FullName -join ', ')" }

Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Package: $zip ($mb MB)"
Write-Host "SHA-256: $hash"
