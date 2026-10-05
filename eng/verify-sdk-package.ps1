[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/sdk-package-verification')
$ErrorActionPreference = 'Stop'
$sdkProject = Join-Path $PSScriptRoot '../src/MirrorPulse.Adapter.Sdk/MirrorPulse.Adapter.Sdk.csproj'
$root = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
$feed = Join-Path $root 'feed'
New-Item -ItemType Directory -Path $feed -Force | Out-Null
& dotnet restore $sdkProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'SDK locked restore failed.' }
& dotnet pack $sdkProject -c Release --no-restore -o $feed
if ($LASTEXITCODE -ne 0) { throw 'SDK pack failed.' }
$packages = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File)
if ($packages.Count -ne 1) { throw 'Expected exactly one SDK package.' }
$zip = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $allowed = @('_rels/.rels', 'MirrorPulse.Adapter.Sdk.nuspec', 'README.md', 'LICENSE', '[Content_Types].xml', 'lib/net10.0-windows7.0/MirrorPulse.Adapter.Sdk.dll')
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -notin $allowed -and $entry.FullName -notmatch '^package/services/metadata/core-properties/(?:[a-f0-9]+|nuget)\.psmdcp$') {
            throw "Unexpected SDK package entry: $($entry.FullName)"
        }
    }
    if (-not $zip.GetEntry('LICENSE')) { throw 'SDK package must include its Apache-2.0 text.' }
} finally { $zip.Dispose() }
$consumer = Join-Path $root 'consumer'
New-Item -ItemType Directory -Path $consumer -Force | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup><Compile Include="Program.cs" /><PackageReference Include="MirrorPulse.Adapter.Sdk" Version="[0.2.0]" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj') -Encoding utf8
@'
using System;
using MirrorPulse.Adapter.Sdk;
Console.WriteLine(typeof(AdapterControlChannel).Assembly.GetName().Name);
'@ | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs') -Encoding utf8
$consumerProject = Join-Path $consumer 'Consumer.csproj'
& dotnet restore $consumerProject --source $feed --packages (Join-Path $root 'packages') -p:ManagePackageVersionsCentrally=false
if ($LASTEXITCODE -ne 0) { throw 'New SDK consumer restore failed.' }
& dotnet restore $consumerProject --locked-mode --source $feed --packages (Join-Path $root 'packages') -p:ManagePackageVersionsCentrally=false
if ($LASTEXITCODE -ne 0) { throw 'New SDK consumer locked restore failed.' }
& dotnet run --project $consumerProject -c Release --no-restore -p:ManagePackageVersionsCentrally=false
if ($LASTEXITCODE -ne 0) { throw 'New SDK consumer failed.' }
Write-Output "SDK package verified: $($packages[0].Name)"
