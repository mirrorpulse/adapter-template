[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Stable','Preview')][string]$Channel,
      [ValidateSet('Breaking','Feature','Fix')][string]$Bump='Fix',
      [string]$VersionsFile, [string]$OutputPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'sdk-version-policy.ps1')
$packageId='MirrorPulse.Adapter.Sdk'
if ($VersionsFile) {
    $fixture=Get-Content -LiteralPath $VersionsFile -Raw | ConvertFrom-Json
    $nuget=@($fixture.nuget)
    $github=@($fixture.github)
} else {
    try {
        $response=Invoke-RestMethod -Uri 'https://api.nuget.org/v3-flatcontainer/mirrorpulse.adapter.sdk/index.json'
        $nuget=@($response.versions)
    } catch {
        if ([int]$_.Exception.Response.StatusCode -ne 404) { throw 'Unable to query published NuGet SDK versions.' }
        $nuget=@()
    }
    $github=@()
    for ($page=1; $page -le 20; $page++) {
        $releases=@(Invoke-RestMethod -Uri "https://api.github.com/repos/MirrorPulse/adapter-template/releases?per_page=100&page=$page")
        foreach ($release in $releases) {
            if (-not $release.draft -and $release.tag_name.StartsWith('sdk-v', [StringComparison]::Ordinal)) {
                $github += $release.tag_name.Substring(5)
            }
        }
        if ($releases.Count -lt 100) { break }
        if ($page -eq 20) { throw 'SDK release history exceeds the bounded query; review the resolver before publishing.' }
    }
}
$versions=@(foreach($value in @($nuget)+@($github)) {
    # Ignore other prerelease channels as CfSharp does; never normalize an unsafe version into a tag.
    if ($value -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview\.([1-9][0-9]*))?$') { continue }
    $text=Get-SdkReleaseVersion $value
    $parts=$text -split '-preview\.'
    [pscustomobject]@{base=[Version]$parts[0]; preview=$(if($parts.Count -eq 2){[long]$parts[1]}else{$null});text=$text}
})
$stable=$versions | Where-Object {$null -eq $_.preview} | Sort-Object base -Descending | Select-Object -First 1
$base=if($stable){$stable.base}else{[Version]'0.0.0'}
if ($Channel -eq 'Stable') {
    $version=switch($Bump) {
        'Breaking' {"$($base.Major+1).0.0"}
        'Feature' {"$($base.Major).$($base.Minor+1).0"}
        'Fix' {"$($base.Major).$($base.Minor).$($base.Build+1)"}
    }
} else {
    $preview=$versions | Where-Object {$null -ne $_.preview} | Sort-Object base,preview -Descending | Select-Object -First 1
    $version=if($preview -and $preview.base -gt $base){"$($preview.base)-preview.$($preview.preview+1)"}else{"$($base.Major).$($base.Minor+1).0-preview.1"}
}
$null=Get-SdkReleaseVersion $version
$result=[ordered]@{schemaVersion=1;packageId=$packageId;channel=$Channel.ToLowerInvariant();bump=$Bump.ToLowerInvariant();
    version=$version;latestStable=$(if($stable){$stable.text}else{$null});nugetVersions=$nuget;githubVersions=$github;
    reference='https://github.com/MirrorPulse/CfSharp/blob/f640b353eb2f18f0abba80c53feef123d1d80e49/eng/resolve-nuget-version.ps1'}
$json=$result | ConvertTo-Json -Depth 6
if($OutputPath){New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))) -Force | Out-Null; $json | Set-Content -LiteralPath $OutputPath -Encoding utf8}
$json
