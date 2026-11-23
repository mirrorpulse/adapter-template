[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$LocalCandidateDirectory
)

$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $package = (Resolve-Path -LiteralPath $PackagePath).Path
    & (Join-Path $PSScriptRoot 'verify-adapter-signature.ps1') -PackagePath $package | Write-Host
    $settings = Get-Content -LiteralPath 'eng/release-settings.json' -Raw | ConvertFrom-Json
    [xml]$project = Get-Content -LiteralPath $settings.workerProject -Raw
    $reference = $project.SelectSingleNode("//PackageReference[@Include='MirrorPulse.Adapter.Sdk']")
    if ($null -eq $reference -or $reference.Version -cnotmatch '^\[([^\[\]]+)\]$') {
        throw 'The template must use an exact SDK package version.'
    }
    $sdkVersion = $Matches[1]
    $runtime = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
    $output = Join-Path (Join-Path $PSScriptRoot '../artifacts/conformance') ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $runner = & (Join-Path $PSScriptRoot 'receive-sdk-conformance.ps1') -SdkVersion $sdkVersion -Runtime $runtime `
        -OutputDirectory (Join-Path $output 'sdk') -LocalCandidateDirectory $LocalCandidateDirectory
    $payload = Join-Path $output 'package'
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $payload)
    $manifest = Get-Content -LiteralPath (Join-Path $payload 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 2 -or @($manifest.rootDefinitions).Count -ne 1 -or $manifest.instancePolicy.maximumRootDefinitions -ne 1) {
        throw 'The sample package must declare exactly one root.'
    }
    $entryPoint = [string]$manifest.entrypoints.$runtime
    & $runner --worker (Join-Path $payload $entryPoint) --transfer-cache (Join-Path $output 'transfers') `
        --single-root $manifest.rootDefinitions[0].key
    if ($LASTEXITCODE -ne 0) { throw 'The signed sample Worker failed native single-root conformance.' }
}
finally { Pop-Location }
