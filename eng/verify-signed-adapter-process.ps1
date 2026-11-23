[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssetDirectory, [Parameter(Mandatory)][string]$SourceSha,
    [Parameter(Mandatory)][string]$ProductRepositoryPath)
$ErrorActionPreference = 'Stop'
$assets = [IO.Path]::GetFullPath($AssetDirectory)
$product = [IO.Path]::GetFullPath($ProductRepositoryPath)
& (Join-Path $PSScriptRoot 'verify-adapter-release-assets.ps1') -AssetDirectory $assets -SourceSha $SourceSha
$manifest = Get-Content -LiteralPath (Join-Path $assets 'provider-release.json') -Raw | ConvertFrom-Json
$package = Join-Path $assets (@($manifest.files | Where-Object { $_.name.EndsWith('.mpadapter', [StringComparison]::Ordinal) })[0].name)
$runtime = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
& (Join-Path $PSScriptRoot 'verify-template-worker.ps1') -PackagePath $package
& (Join-Path $PSScriptRoot 'verify-template-product.ps1') -PackagePath $package -ProductRepositoryPath $product `
    -ResultsDirectory (Join-Path $assets "process-$runtime") -OfficialSignature:([bool]$manifest.publish)
