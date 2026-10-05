. (Join-Path $PSScriptRoot 'release-source-policy.ps1')

function Get-SdkStableClassification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$SourceSha,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$PullRequests
    )
    Get-MergedReleaseClassification -Repository $Repository -SourceSha $SourceSha -PullRequests $PullRequests
}
