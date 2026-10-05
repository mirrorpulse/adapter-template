$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'sdk-version-policy.ps1')
$root=Join-Path ([IO.Path]::GetFullPath('artifacts/sdk-version-verification')) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$cases=@(
    @{nuget=@();github=@();channel='Preview';bump='Fix';expected='0.1.0-preview.1'},
    @{nuget=@();github=@('0.2.0');channel='Stable';bump='Fix';expected='0.2.1'},
    @{nuget=@('0.2.0','0.3.0-preview.2');github=@();channel='Preview';bump='Fix';expected='0.3.0-preview.3'},
    @{nuget=@('0.3.0','0.3.0-preview.9');github=@();channel='Preview';bump='Fix';expected='0.4.0-preview.1'},
    @{nuget=@('0.2.0');github=@('0.2.1');channel='Stable';bump='Feature';expected='0.3.0'},
    @{nuget=@('0.2.0');github=@();channel='Stable';bump='Breaking';expected='1.0.0'}
)
foreach($case in $cases){
    $fixture=Join-Path $root ([Guid]::NewGuid().ToString('N')+'.json')
    @{nuget=$case.nuget;github=$case.github}|ConvertTo-Json|Set-Content -LiteralPath $fixture -Encoding utf8
    $result=& (Join-Path $PSScriptRoot 'resolve-sdk-version.ps1') -Channel $case.channel -Bump $case.bump -VersionsFile $fixture | ConvertFrom-Json
    if($result.version -cne $case.expected){throw 'The SDK version policy differs from the CfSharp release model.'}
}
foreach($value in @('01.2.3','1.2.3.4','1.2.3-preview.0','1.2.3;throw 1','../1.2.3',"1.2.3`n")){
    $rejected=$false;try{$null=Get-SdkReleaseVersion $value}catch{$rejected=$true}
    if(-not $rejected){throw 'Unsafe SDK version accepted.'}
}
Write-Host 'SDK stable/preview increments, published GitHub baseline and hostile version cases passed.'
