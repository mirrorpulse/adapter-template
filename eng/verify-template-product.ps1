[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ProductRepositoryPath,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [switch]$OfficialSignature
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$product = (Resolve-Path -LiteralPath $ProductRepositoryPath).Path
$results = [IO.Path]::GetFullPath($ResultsDirectory)
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-settings.json') -Raw | ConvertFrom-Json
$source = git -C $product rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $settings.productVerifierSource -cnotmatch '\A[0-9a-f]{40}\z' -or
    $source -cne $settings.productVerifierSource) {
    throw 'The product verifier source differs from the pinned revision.'
}
$names = @('MP_TEMPLATE_PACKAGE', 'MP_TEMPLATE_PUBLIC_KEY', 'MP_TEMPLATE_OFFICIAL_SIGNED')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location -LiteralPath $product
try {
    $env:MP_TEMPLATE_PACKAGE = $package
    $env:MP_TEMPLATE_PUBLIC_KEY = $package + '.public.pem'
    $env:MP_TEMPLATE_OFFICIAL_SIGNED = $OfficialSignature.IsPresent.ToString().ToLowerInvariant()
    & pwsh -NoProfile -File eng/restore-adapter-sdk.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Pinned product SDK verification failed.' }

    $project = 'tests/MirrorPulse.Core.Tests/MirrorPulse.Core.Tests.csproj'
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Pinned product locked restore failed.' }
    & dotnet test $project -c Release --no-restore --filter FullyQualifiedName~SignedTemplateWorkerProcessTests `
        --logger 'trx;LogFileName=template.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw 'Production signature/install/Supervisor conformance failed.' }

    [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'template.trx') -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ($counts.total -ne 1 -or $counts.executed -ne 1 -or $counts.passed -ne 1 -or $counts.notExecuted -ne 0) {
        throw 'Signed process conformance must execute with no skips.'
    }
}
finally {
    Pop-Location
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
