[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssetDirectory,
      [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceSha,
      [switch]$DryRun)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'verify-sdk-assets.ps1') -AssetDirectory $AssetDirectory -SourceSha $SourceSha
$manifest=Get-Content -LiteralPath (Join-Path $AssetDirectory 'sdk-release.json') -Raw | ConvertFrom-Json
$package=Join-Path ([IO.Path]::GetFullPath($AssetDirectory)) "MirrorPulse.Adapter.Sdk.$($manifest.version).nupkg"
if((Get-Item -LiteralPath $package).Length -gt 4MB){throw 'SDK NuGet package exceeds the supported bound.'}
$zip=[IO.Compression.ZipFile]::OpenRead($package)
try{
    $entries=@($zip.Entries | Where-Object FullName -CEQ 'MirrorPulse.Adapter.Sdk.nuspec')
    if($entries.Count -ne 1 -or $entries[0].Length -gt 64KB){throw 'SDK package metadata is missing, duplicate or oversized.'}
    $reader=[IO.StreamReader]::new($entries[0].Open())
    try{[xml]$nuspec=$reader.ReadToEnd()}finally{$reader.Dispose()}
    $metadata=$nuspec.package.metadata
    if($metadata.id -cne 'MirrorPulse.Adapter.Sdk' -or $metadata.version -cne $manifest.version -or
        $metadata.license.type -cne 'expression' -or $metadata.license.'#text' -cne 'Apache-2.0' -or
        $metadata.repository.url -cne 'https://github.com/MirrorPulse/adapter-template' -or
        $metadata.repository.commit -cne $SourceSha -or -not $zip.GetEntry('lib/net10.0-windows7.0/MirrorPulse.Adapter.Sdk.dll')){
        throw 'SDK package identity, source, license or public assembly differs from the release inventory.'
    }
}finally{$zip.Dispose()}
if($DryRun){Write-Host "SDK $($manifest.version) NuGet dry run passed; no credential or remote write was used.";return}
if([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)){throw 'NuGet/login must provide a temporary NUGET_API_KEY immediately before publication.'}
& dotnet nuget push $package --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json
if($LASTEXITCODE -ne 0){throw 'NuGet publication failed; inspect the retained evidence before retrying. Existing versions are not skipped or overwritten.'}
[ordered]@{schemaVersion=1;packageId='MirrorPulse.Adapter.Sdk';version=$manifest.version;sourceSha=$SourceSha;
    sha256=(Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant();publishedAt=[DateTimeOffset]::UtcNow.ToString('O');source='https://api.nuget.org/v3/index.json'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($AssetDirectory))) 'sdk-nuget-evidence.json') -Encoding utf8
