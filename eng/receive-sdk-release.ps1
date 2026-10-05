[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'sdk-version-policy.ps1')
$version=Get-SdkReleaseVersion $Version
$tag="sdk-v$version"
$release=Invoke-RestMethod -Uri "https://api.github.com/repos/MirrorPulse/adapter-template/releases/tags/$tag"
if($release.draft -or $release.tag_name -cne $tag -or $release.prerelease -ne $version.Contains('-preview.')) { throw 'The SDK release channel or tag is invalid.' }
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $root){throw 'The download output must be a new directory.'}
New-Item -ItemType Directory -Path $root -Force | Out-Null
$names=@('sdk-release.json',"MirrorPulse.Adapter.Sdk.$version.nupkg","MirrorPulse.Worker.Spec-$version.zip",
    "MirrorPulse.Adapter.Conformance-$version-win-x64.zip","MirrorPulse.Adapter.Conformance-$version-win-arm64.zip")
if($release.assets.Count -ne $names.Count){throw 'Unexpected SDK release asset inventory.'}
foreach($name in $names){
    $assets=@($release.assets | Where-Object name -CEQ $name)
    if($assets.Count -ne 1 -or $assets[0].size -le 0 -or $assets[0].size -gt 128MB){throw 'Missing, duplicate or oversized SDK release asset.'}
    $uri=[Uri]$assets[0].browser_download_url
    $expected="/MirrorPulse/adapter-template/releases/download/$tag/$name"
    if($uri.Scheme -cne 'https' -or $uri.Host -cne 'github.com' -or $uri.AbsolutePath -ine $expected){throw 'Unexpected SDK release download address.'}
    Invoke-WebRequest -Uri $uri.AbsoluteUri -OutFile (Join-Path $root $name) -MaximumRetryCount 3 -RetryIntervalSec 2
}
$manifest=Get-Content -LiteralPath (Join-Path $root 'sdk-release.json') -Raw | ConvertFrom-Json
$reference=Invoke-RestMethod -Uri "https://api.github.com/repos/MirrorPulse/adapter-template/git/ref/tags/$tag"
$object=$reference.object
for($depth=0;$object.type -ceq 'tag' -and $depth -lt 4;$depth++){
    $object=(Invoke-RestMethod -Uri "https://api.github.com/repos/MirrorPulse/adapter-template/git/tags/$($object.sha)").object
}
if($object.type -cne 'commit' -or $object.sha -cne $manifest.sourceSha -or $manifest.version -cne $version){throw 'The SDK manifest is not bound to its release tag source.'}
& (Join-Path $PSScriptRoot 'verify-sdk-assets.ps1') -AssetDirectory $root -SourceSha $object.sha
Write-Host "Downloaded and verified immutable SDK $version from source $($object.sha)."
