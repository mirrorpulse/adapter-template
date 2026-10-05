[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot 'verify-adapter-version.ps1')
& (Join-Path $PSScriptRoot 'verify-adapter-publishing.ps1')
& (Join-Path $PSScriptRoot 'verify-sdk-version.ps1')
& (Join-Path $PSScriptRoot 'verify-sdk-publishing.ps1')
$projects = @("src/MirrorPulse.Adapter.Sdk/MirrorPulse.Adapter.Sdk.csproj", "samples/MirrorPulse.Adapter.SampleWorker/MirrorPulse.Adapter.SampleWorker.csproj", "tools/MirrorPulse.Adapter.Conformance/MirrorPulse.Adapter.Conformance.csproj", "tests/MirrorPulse.Adapter.ContractTests/MirrorPulse.Adapter.ContractTests.csproj")
foreach ($project in $projects) {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Restore failed for $project." }
    & dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $project." }
    & dotnet format $project --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw "Formatting failed for $project." }
}
& (Join-Path $PSScriptRoot 'verify-sdk-package.ps1')
& dotnet run --project tests/MirrorPulse.Adapter.ContractTests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'SDK contract checks failed.' }
& (Join-Path $PSScriptRoot 'verify-wire-conformance.ps1')
