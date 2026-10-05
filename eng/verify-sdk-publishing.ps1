[CmdletBinding()]
param([string]$AssetDirectory)
$ErrorActionPreference='Stop'
foreach($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1'){
    $tokens=$null;$errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Invalid release PowerShell syntax.'}
}
$workflow=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.github/workflows/sdk-release.yml') -Raw
foreach($action in [regex]::Matches($workflow,'uses:\s+[^@\s]+@([^\s]+)')){
    if($action.Groups[1].Value -cnotmatch '\A[0-9a-f]{40}\z'){throw 'Release Actions require immutable full commit SHAs.'}
}
foreach($run in [regex]::Matches($workflow,'(?ms)^\s{8}run:.*?(?=^\s{6}- |^\s{2}\S|\z)')){
    if($run.Value.Contains('${{')){throw 'Workflow inputs must enter executable scripts as environment data.'}
}
$names=@('MP_SDK_EVENT','MP_SDK_PUBLISH','MP_SDK_CHANNEL','MP_SDK_EXISTING_VERSION','MP_SDK_CONFIRM','GITHUB_REF','GITHUB_OUTPUT')
$previous=@{};foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try{
    foreach($case in @(
        @{event='workflow_dispatch';channel='Preview';ref='refs/heads/main';existing='';confirm='PUBLISH'},
        @{event='workflow_dispatch';channel='Stable';ref='refs/heads/main';existing='';confirm='PUBLISH'},
        @{event='workflow_dispatch';channel='Preview';ref='refs/heads/develop';existing='0.2.0';confirm='PUBLISH'},
        @{event='workflow_dispatch';channel='Preview';ref='refs/heads/develop';existing='';confirm=''})){
        $env:MP_SDK_EVENT=$case.event;$env:MP_SDK_PUBLISH='true';$env:MP_SDK_CHANNEL=$case.channel
        $env:MP_SDK_EXISTING_VERSION=$case.existing;$env:MP_SDK_CONFIRM=$case.confirm;$env:GITHUB_REF=$case.ref
        $rejected=$false;try{& (Join-Path $PSScriptRoot 'resolve-sdk-publication.ps1')}catch{$rejected=$true}
        if(-not $rejected){throw 'A publication bypass was accepted.'}
    }
}finally{foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}}
if($AssetDirectory){
    $manifest=Get-Content -LiteralPath (Join-Path $AssetDirectory 'sdk-release.json') -Raw | ConvertFrom-Json
    & (Join-Path $PSScriptRoot 'publish-sdk-nuget.ps1') -AssetDirectory $AssetDirectory -SourceSha $manifest.sourceSha -DryRun
    $root=Join-Path ([IO.Path]::GetFullPath('artifacts/sdk-publishing-rejection')) ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Get-ChildItem -LiteralPath $AssetDirectory -File | Copy-Item -Destination $root
    $package=Join-Path $root "MirrorPulse.Adapter.Sdk.$($manifest.version).nupkg"
    $bytes=[IO.File]::ReadAllBytes($package);$bytes[0]=$bytes[0] -bxor 1;[IO.File]::WriteAllBytes($package,$bytes)
    $rejected=$false;try{& (Join-Path $PSScriptRoot 'publish-sdk-nuget.ps1') -AssetDirectory $root -SourceSha $manifest.sourceSha -DryRun}catch{$rejected=$true}
    if(-not $rejected){throw 'A tampered NuGet package was accepted.'}
}
Write-Host 'SDK publication branch/confirmation boundaries and immutable action references passed.'
