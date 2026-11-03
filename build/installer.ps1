<#
.SYNOPSIS
  Builds dist\MusicFinder-<version>-Setup.exe with Inno Setup (publishes the app first unless -SkipPublish).
#>
param([switch]$SkipPublish, [switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

$dist = Join-Path $repo 'dist'
$source = Join-Path $dist "MusicFinder-$version-win-x64"
if (-not $SkipPublish) {
    $args = @(); if ($SkipTests) { $args += '-SkipTests' }
    & (Join-Path $PSScriptRoot 'publish.ps1') @args -NoPortable
}
if (-not (Test-Path $source)) { throw "Published folder missing: $source" }

& $iscc "/DAppVersion=$version" "/DSourceDir=$source" "/DOutputDir=$dist" (Join-Path $PSScriptRoot 'installer\MusicFinder.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }

$setup = Join-Path $dist "MusicFinder-$version-Setup.exe"
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host "SHA-256: $((Get-FileHash $setup -Algorithm SHA256).Hash)"
