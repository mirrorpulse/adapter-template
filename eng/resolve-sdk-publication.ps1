$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'sdk-version-policy.ps1')
. (Join-Path $PSScriptRoot 'sdk-release-source.ps1')
$existing=[string]$env:MP_SDK_EXISTING_VERSION
$publish=$env:MP_SDK_PUBLISH -ceq 'true'
$channel=[string]$env:MP_SDK_CHANNEL
$bump='Fix'
$source=& git rev-parse HEAD
if($LASTEXITCODE -ne 0){throw 'Cannot identify the checked-out SDK source.'}
if($env:MP_SDK_EVENT -ceq 'push'){
    if($env:GITHUB_REF -cne 'refs/heads/main' -or $source -cne $env:GITHUB_SHA -or
        $source -cnotmatch '\A[0-9a-f]{40}\z' -or
        $env:GITHUB_REPOSITORY -cnotmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z'){
        throw 'Stable SDK publication requires the exact pushed main source.'
    }
    if([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)){throw 'Stable SDK source validation requires a read-only GitHub token.'}
    $headers=@{Authorization="Bearer $env:GITHUB_TOKEN";Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
    try{
        $response=Invoke-RestMethod -Uri "https://api.github.com/repos/$env:GITHUB_REPOSITORY/commits/$source/pulls?per_page=100" -Headers $headers
    }catch{throw 'Unable to verify the merged SDK pull request for this main commit.'}
    $pullRequests=@($response | ForEach-Object {$_})
    if($pullRequests.Count -ge 100){throw 'SDK commit PR history exceeds the bounded query; review before publishing.'}
    $classification=Get-SdkStableClassification -Repository $env:GITHUB_REPOSITORY -SourceSha $source -PullRequests $pullRequests
    $bump=$classification.bump
    Write-Host "Validated merged develop PR #$($classification.pullRequest) for SDK source $source."
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
