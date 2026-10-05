[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssetDirectory, [Parameter(Mandatory)][string]$SourceSha,
    [Parameter(Mandatory)][string]$ProductRepositoryPath)
$ErrorActionPreference = 'Stop'
$assets = [IO.Path]::GetFullPath($AssetDirectory)
$product = [IO.Path]::GetFullPath($ProductRepositoryPath)
& (Join-Path $PSScriptRoot 'verify-adapter-release-assets.ps1') -AssetDirectory $assets -SourceSha $SourceSha
$manifest = Get-Content -LiteralPath (Join-Path $assets 'provider-release.json') -Raw | ConvertFrom-Json
$package = Join-Path $assets (@($manifest.files | Where-Object { $_.name.EndsWith('.mpadapter', [StringComparison]::Ordinal) })[0].name)
$runtime = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
$unpacked = Join-Path ([IO.Path]::GetFullPath('artifacts/signed-process')) ([Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($package, $unpacked)
foreach ($project in @('src/MirrorPulse.Adapter.Sdk/MirrorPulse.Adapter.Sdk.csproj', 'tools/MirrorPulse.Adapter.Conformance/MirrorPulse.Adapter.Conformance.csproj')) {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Conformance locked restore failed.' }
    & dotnet build $project -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Conformance build failed.' }
}
& dotnet run --project tools/MirrorPulse.Adapter.Conformance -c Release --no-build -- --worker (Join-Path $unpacked "worker/$runtime/MirrorPulse.Adapter.Worker.exe") --transfer-cache (Join-Path $unpacked 'transfers')
if ($LASTEXITCODE -ne 0) { throw 'Actual signed Worker wire conformance failed.' }
$names = @('MP_TEMPLATE_PACKAGE', 'MP_TEMPLATE_PUBLIC_KEY', 'MP_TEMPLATE_OFFICIAL_SIGNED')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:MP_TEMPLATE_PACKAGE = $package
    $env:MP_TEMPLATE_PUBLIC_KEY = $package + '.public.pem'
    $env:MP_TEMPLATE_OFFICIAL_SIGNED = ([bool]$manifest.publish).ToString().ToLowerInvariant()
    Push-Location $product
    try {
        & pwsh -NoProfile -File eng/restore-adapter-sdk.ps1
        if ($LASTEXITCODE -ne 0) { throw 'Pinned product SDK verification failed.' }
        & dotnet restore tests/MirrorPulse.Core.Tests/MirrorPulse.Core.Tests.csproj --locked-mode --packages artifacts/provider-process-packages
        if ($LASTEXITCODE -ne 0) { throw 'Pinned product locked restore failed.' }
        $results = Join-Path $assets "process-$runtime"
        & dotnet test tests/MirrorPulse.Core.Tests/MirrorPulse.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SignedTemplateWorkerProcessTests --logger 'trx;LogFileName=template.trx' --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw 'Production signature/install/Supervisor conformance failed.' }
        [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'template.trx') -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        if ($counts.total -ne 1 -or $counts.executed -ne 1 -or $counts.passed -ne 1 -or $counts.notExecuted -ne 0) { throw 'Signed process conformance must execute with no skips.' }
    } finally { Pop-Location }
} finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') } }
