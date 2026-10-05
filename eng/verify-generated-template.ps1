[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProductRepositoryPath)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$product = [IO.Path]::GetFullPath($ProductRepositoryPath)
if (-not (Test-Path -LiteralPath (Join-Path $product 'MirrorPulse.sln'))) { throw 'A pinned MirrorPulse checkout is required.' }
$generated = Join-Path $repository ('artifacts/generated/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $generated -Force | Out-Null
$files = @(& git -C $repository ls-files)
if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw 'Cannot enumerate template source.' }
foreach ($file in $files) {
    . (Join-Path $PSScriptRoot 'release-policy.ps1')
    Assert-AdapterPackagePath $file
    $destination = Join-Path $generated $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $file) -Destination $destination
}
& git -C $generated init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Generated repository initialization failed.' }
$previousVersion = $env:MP_RELEASE_VERSION
$previousEvent = $env:MP_RELEASE_EVENT
$previousPackage = $env:MP_TEMPLATE_PACKAGE
$previousKey = $env:MP_TEMPLATE_PUBLIC_KEY
Push-Location $generated
try {
    & ./eng/verify.ps1
    $env:MP_RELEASE_EVENT = 'workflow_dispatch'
    $env:MP_RELEASE_VERSION = '0.1.0'
    $output = @(& ./eng/pack-adapter.ps1)
    $package = [string]$output[-1]
    & ./eng/sign-adapter.ps1 -PackagePath $package -DryRun
    $env:MP_TEMPLATE_PACKAGE = $package
    $env:MP_TEMPLATE_PUBLIC_KEY = $package + '.public.pem'
    Push-Location $product
    try {
        & dotnet restore MirrorPulse.sln --locked-mode --packages artifacts/preview3-public-cache --source https://api.nuget.org/v3/index.json
        if ($LASTEXITCODE -ne 0) { throw 'Pinned product locked restore failed.' }
        $results = Join-Path $generated 'artifacts/product-conformance'
        & dotnet test tests/MirrorPulse.Core.Tests/MirrorPulse.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SignedTemplateWorkerProcessTests --logger 'trx;LogFileName=template.trx' --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw 'Production package conformance failed.' }
        [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'template.trx') -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        if ($counts.total -ne 1 -or $counts.executed -ne 1 -or $counts.passed -ne 1) { throw 'The actual package gate must execute without skips.' }
    } finally { Pop-Location }
} finally {
    Pop-Location
    $env:MP_RELEASE_VERSION = $previousVersion
    $env:MP_RELEASE_EVENT = $previousEvent
    $env:MP_TEMPLATE_PACKAGE = $previousPackage
    $env:MP_TEMPLATE_PUBLIC_KEY = $previousKey
}
Write-Host 'Generated repository, signed installation and actual Worker conformance passed.'
