[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SdkVersion,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$LocalCandidateDirectory
)

$ErrorActionPreference = 'Stop'
if ($SdkVersion -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview\.[1-9][0-9]*)?\z') {
    throw 'The conformance version must be an exact SDK release version.'
}
$directory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $directory | Out-Null
$assetName = "MirrorPulse.Adapter.Conformance-$SdkVersion-$Runtime.zip"
$manifestName = 'sdk-release.json'
if ($LocalCandidateDirectory) {
    foreach ($entry in @($assetName, $manifestName)) {
        Copy-Item -LiteralPath (Join-Path $LocalCandidateDirectory $entry) -Destination $directory
    }
} else {
    gh release download "sdk-v$SdkVersion" --repo mirrorpulse/adapter-sdk `
        --pattern $assetName --pattern $manifestName --dir $directory
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the pinned SDK conformance runner.' }
}

$manifest = Get-Content -LiteralPath (Join-Path $directory $manifestName) -Raw | ConvertFrom-Json
$archive = Get-Item -LiteralPath (Join-Path $directory $assetName)
$selected = @($manifest.assets | Where-Object name -CEQ $assetName)
if ($manifest.schemaVersion -ne 1 -or $manifest.repository -ine 'mirrorpulse/adapter-sdk' -or
    $manifest.version -cne $SdkVersion -or $manifest.tag -cne "sdk-v$SdkVersion" -or
    $manifest.sourceSha -cnotmatch '\A[0-9a-f]{40}\z' -or $selected.Count -ne 1 -or
    $selected[0].length -ne $archive.Length -or $selected[0].sha256 -cne (Get-FileHash -LiteralPath $archive.FullName).Hash.ToLowerInvariant()) {
    throw 'The conformance runner differs from its versioned SDK inventory.'
}

$runnerDirectory = Join-Path $directory 'runner'
[IO.Compression.ZipFile]::ExtractToDirectory($archive.FullName, $runnerDirectory)
return Join-Path $runnerDirectory 'MirrorPulse.Adapter.Conformance.exe'
