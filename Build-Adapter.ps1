[CmdletBinding()]
param(
    [string]$Version,
    [switch]$ReleaseSignature
)

$ErrorActionPreference = 'Stop'
$previousVersion = $env:MP_RELEASE_VERSION
Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not $Version) {
        $manifest = Get-Content -LiteralPath 'eng/release-manifest.json' -Raw | ConvertFrom-Json
        $Version = $manifest.version
    }
    $env:MP_RELEASE_VERSION = $Version
    $output = @(& './eng/pack-adapter.ps1')
    $package = [string]$output[-1]
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw 'The package build produced no file.' }

    & './eng/sign-adapter.ps1' -PackagePath $package -DryRun:(-not $ReleaseSignature)
    Write-Output $package
}
finally {
    $env:MP_RELEASE_VERSION = $previousVersion
    Pop-Location
}
