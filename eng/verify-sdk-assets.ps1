[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssetDirectory,
      [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
      [switch]$ExecuteRunner)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sdk-version-policy.ps1')
$root = [IO.Path]::GetFullPath($AssetDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $root 'sdk-release.json') -Raw | ConvertFrom-Json
$version = Get-SdkReleaseVersion $manifest.version
$names = @("MirrorPulse.Adapter.Sdk.$version.nupkg", "MirrorPulse.Worker.Spec-$version.zip",
    "MirrorPulse.Adapter.Conformance-$version-win-x64.zip", "MirrorPulse.Adapter.Conformance-$version-win-arm64.zip")
if ($manifest.schemaVersion -ne 1 -or $manifest.repository -cne 'MirrorPulse/adapter-template' -or
    $manifest.sourceSha -cne $SourceSha -or $manifest.tag -cne "sdk-v$version" -or $manifest.assets.Count -ne 4 -or
    @(Get-ChildItem -LiteralPath $root -File).Count -ne 5) { throw 'The fixed release manifest is invalid.' }
foreach ($name in $names) {
    $asset = @($manifest.assets | Where-Object name -CEQ $name)
    if ($asset.Count -ne 1 -or $asset[0].sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Missing or duplicate fixed asset.' }
    $path = Join-Path $root $name
    if ((Get-Item -LiteralPath $path).Length -ne $asset[0].length -or
        (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $asset[0].sha256) { throw 'SDK asset hash mismatch.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $license = if ($name -like 'MirrorPulse.Adapter.Conformance-*') { 'MirrorPulse-LICENSE.txt' } else { 'LICENSE' }
        $entry = $archive.GetEntry($license)
        if (-not $entry) { throw 'A distributed SDK asset is missing its own license.' }
        $stream = $entry.Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($actual -cne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../LICENSE')).Hash) { throw 'SDK license text changed during packaging.' }
    } finally { $archive.Dispose() }
}
if ($ExecuteRunner) {
    $runtime = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
    $runRoot = Join-Path ([IO.Path]::GetFullPath('artifacts/sdk-runner-verification')) ([Guid]::NewGuid().ToString('N'))
    $runner = Join-Path $runRoot 'runner'
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $root "MirrorPulse.Adapter.Conformance-$version-$runtime.zip"), $runner)
    & dotnet restore samples/MirrorPulse.Adapter.SampleWorker --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Sample restore failed.' }
    $worker = Join-Path $runRoot 'worker'
    & dotnet publish samples/MirrorPulse.Adapter.SampleWorker -c Release -r $runtime --self-contained false --no-restore -o $worker
    if ($LASTEXITCODE -ne 0) { throw 'Sample publish failed.' }
    & (Join-Path $runner 'MirrorPulse.Adapter.Conformance.exe') --worker (Join-Path $worker 'MirrorPulse.Adapter.SampleWorker.exe') --transfer-cache (Join-Path $runRoot 'transfers')
    if ($LASTEXITCODE -ne 0) { throw 'Published runner process conformance failed.' }
}
Write-Host 'Fixed SDK assets and SHA256 inventory verified.'
