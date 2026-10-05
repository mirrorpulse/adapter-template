$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'sdk-version-policy.ps1')
$existing=[string]$env:MP_SDK_EXISTING_VERSION
$publish=$env:MP_SDK_PUBLISH -ceq 'true'
$channel=[string]$env:MP_SDK_CHANNEL
$bump='Fix'
$source=& git rev-parse HEAD
if($LASTEXITCODE -ne 0){throw 'Cannot identify the checked-out SDK source.'}
if($env:MP_SDK_EVENT -ceq 'pull_request_target'){
    if($env:MP_SDK_MERGED -cne 'true' -or $env:MP_SDK_BASE_BRANCH -cne 'main' -or
        $env:MP_SDK_HEAD_BRANCH -cne 'develop' -or $env:MP_SDK_HEAD_REPOSITORY -cne $env:GITHUB_REPOSITORY -or
        $source -cne $env:MP_SDK_MERGE_SHA){throw 'Stable publication requires the merged develop pull request source.'}
    $labels=@($env:MP_SDK_LABELS -split ',' | Where-Object {$_ -cin @('breaking','feature','fix')})
    if($labels.Count -ne 1){throw 'Stable publication requires exactly one release classification.'}
    $bump=$labels[0]
    $channel='Stable';$publish=$true;$existing=''
}elseif($env:MP_SDK_EVENT -ceq 'workflow_dispatch'){
    if($channel -cnotin @('Stable','Preview')){throw 'Invalid SDK publication channel.'}
    if($publish -and $env:MP_SDK_CONFIRM -cne 'PUBLISH'){throw 'Publication requires the exact PUBLISH confirmation.'}
    if($publish -and (($channel -ceq 'Stable' -and $env:GITHUB_REF -cne 'refs/heads/main') -or
        ($channel -ceq 'Preview' -and $env:GITHUB_REF -cne 'refs/heads/develop'))){throw 'Publication channel does not match the trusted branch.'}
    if($publish -and $channel -ceq 'Stable' -and -not $existing){throw 'New stable versions publish only after a classified develop pull request is merged.'}
}else{throw 'Unsupported SDK publication event.'}
if($existing){
    $version=Get-SdkReleaseVersion $existing
    if(($version.Contains('-preview.') -and $channel -cne 'Preview') -or
        (-not $version.Contains('-preview.') -and $channel -cne 'Stable')){throw 'Existing SDK version does not match its channel.'}
    & (Join-Path $PSScriptRoot 'receive-sdk-release.ps1') -Version $version -OutputDirectory artifacts/sdk-release
    $source=(Get-Content -LiteralPath artifacts/sdk-release/sdk-release.json -Raw | ConvertFrom-Json).sourceSha
    & (Join-Path $PSScriptRoot 'publish-sdk-nuget.ps1') -AssetDirectory artifacts/sdk-release -SourceSha $source -DryRun
}else{
    $result=& (Join-Path $PSScriptRoot 'resolve-sdk-version.ps1') -Channel $channel -Bump $bump -OutputPath artifacts/sdk-version.json | ConvertFrom-Json
    $version=$result.version
}
foreach($entry in @("version=$version","source_sha=$source","channel=$($channel.ToLowerInvariant())",
    "existing=$([bool]$existing)".ToLowerInvariant(),"publish=$publish".ToLowerInvariant())){
    $entry | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
