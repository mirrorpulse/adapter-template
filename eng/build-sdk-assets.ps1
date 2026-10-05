[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
      [string]$OutputDirectory = 'artifacts/sdk-release', [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sdk-version-policy.ps1')
$version = Get-SdkReleaseVersion $Version
$actualSource = & git rev-parse HEAD
$changes = @(& git status --porcelain)
if ($LASTEXITCODE -ne 0 -or $actualSource -cne $SourceSha -or $changes.Count -ne 0) { throw 'SDK assets require an exact clean source commit.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Release output must be a new directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
& dotnet restore src/MirrorPulse.Adapter.Sdk --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'SDK restore failed.' }
& dotnet pack src/MirrorPulse.Adapter.Sdk -c Release --no-restore -o $output "-p:Version=$version" "-p:PackageVersion=$version" "-p:RepositoryCommit=$SourceSha"
if ($LASTEXITCODE -ne 0) { throw 'SDK pack failed.' }
& dotnet restore tools/MirrorPulse.Adapter.Conformance --locked-mode -p:SelfContained=true
if ($LASTEXITCODE -ne 0) { throw 'Runner locked restore failed.' }
foreach ($runtime in @('win-x64', 'win-arm64')) {
    $publish = Join-Path (Split-Path -Parent $output) ('sdk-runner/' + [Guid]::NewGuid().ToString('N') + '/' + $runtime)
    & dotnet publish tools/MirrorPulse.Adapter.Conformance -c Release -r $runtime --self-contained true --no-restore -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Runner publish failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE') -Destination (Join-Path $publish 'MirrorPulse-LICENSE.txt')
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, (Join-Path $output "MirrorPulse.Adapter.Conformance-$version-$runtime.zip"))
}
$specification = Join-Path (Split-Path -Parent $output) ('sdk-spec/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $specification -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../spec') | Copy-Item -Destination $specification -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE') -Destination (Join-Path $specification 'LICENSE')
[IO.Compression.ZipFile]::CreateFromDirectory($specification, (Join-Path $output "MirrorPulse.Worker.Spec-$version.zip"))
$assets = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ name = $_.Name; length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
if ($assets.Count -ne 4) { throw 'Expected SDK, specification and two runners.' }
[ordered]@{ schemaVersion=1; version=$version; tag="sdk-v$version"; repository='MirrorPulse/adapter-template'; sourceSha=$SourceSha; assets=$assets } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'sdk-release.json') -Encoding utf8
if ($env:GITHUB_OUTPUT) { "version=$version" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
Write-Host "SDK $version candidate built without publishing."
