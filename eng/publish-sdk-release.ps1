[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssetDirectory, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $AssetDirectory 'sdk-release.json') -Raw | ConvertFrom-Json
& (Join-Path $PSScriptRoot 'verify-sdk-assets.ps1') -AssetDirectory $AssetDirectory -SourceSha $manifest.sourceSha
if ($DryRun) { Write-Host 'SDK publication dry run passed; no remote write occurred.'; return }
$source = & git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $source -cne $manifest.sourceSha -or
    $env:GITHUB_REF -cnotin @('refs/heads/main', 'refs/heads/develop')) { throw 'Only the verified release branch source may publish.' }
if (($manifest.version.Contains('-preview.') -and $env:GITHUB_REF -cne 'refs/heads/develop') -or
    (-not $manifest.version.Contains('-preview.') -and $env:GITHUB_REF -cne 'refs/heads/main')) { throw 'SDK release channel does not match its branch.' }
& gh api "repos/$($manifest.repository)/git/ref/tags/$($manifest.tag)" --silent 2>$null
if ($LASTEXITCODE -eq 0) { throw 'The SDK tag already exists; existing versions cannot be replaced.' }
# Creation itself rejects a concurrent or inaccessible existing tag. Never force a ref.
& gh api "repos/$($manifest.repository)/git/refs" -f "ref=refs/tags/$($manifest.tag)" -f "sha=$($manifest.sourceSha)" --silent
if ($LASTEXITCODE -ne 0) { throw 'SDK tag creation failed.' }
$assets = @(Get-ChildItem -LiteralPath $AssetDirectory -File | ForEach-Object FullName)
$options = @()
if ($manifest.version.Contains('-preview.')) { $options += '--prerelease' }
& gh release create $manifest.tag @assets --repo $manifest.repository --verify-tag --title "MirrorPulse Adapter SDK $($manifest.version)" --notes 'Canonical SDK, language-neutral Worker specification, native conformance runners and SHA256 inventory. Assets are fixed for this version.' --latest=false @options
if ($LASTEXITCODE -ne 0) { throw 'SDK release creation failed; inspect the reserved tag before retrying.' }
