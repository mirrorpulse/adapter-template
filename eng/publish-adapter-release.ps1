[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssetDirectory, [Parameter(Mandatory)][string]$SourceSha, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'verify-adapter-release-assets.ps1') -AssetDirectory $AssetDirectory -SourceSha $SourceSha
$manifest = Get-Content -LiteralPath (Join-Path $AssetDirectory 'provider-release.json') -Raw | ConvertFrom-Json
$checkedSource = & git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $checkedSource -cne $SourceSha) { throw 'Publication must use the exact verified source checkout.' }
if ($DryRun) { Write-Host 'Provider publication dry run passed; no remote write occurred.'; return }
if (-not $manifest.publish -or $env:GITHUB_REPOSITORY -cne $manifest.repository -or
    ($manifest.channel -ceq 'stable' -and ($env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -cne 'push')) -or
    ($manifest.channel -ceq 'preview' -and ($env:GITHUB_REF -cne 'refs/heads/develop' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch'))) {
    throw 'Publication candidate does not match the trusted branch and event.'
}
$assets = @($manifest.files | ForEach-Object { Join-Path $AssetDirectory $_.name })
$assets += Join-Path $AssetDirectory 'provider-release.json'
& gh api --method POST "repos/$($manifest.repository)/git/refs" -f "ref=refs/tags/$($manifest.tag)" -f "sha=$SourceSha" --silent
if ($LASTEXITCODE -ne 0) { throw 'Immutable release tag creation failed; do not overwrite an existing tag.' }
$options = if ($manifest.channel -ceq 'preview') { @('--prerelease', '--latest=false') } else { @('--latest=true') }
& gh release create $manifest.tag @assets --repo $manifest.repository --verify-tag --title "Adapter $($manifest.version)" --notes 'Signed process Adapter with native x64 and ARM64 conformance, production installation verification, and a fixed SHA256 inventory.' @options
if ($LASTEXITCODE -ne 0) { throw 'Release publication failed; preserve the original candidate and tag for manual recovery.' }
