[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null
    $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw 'A release script has invalid PowerShell syntax.' }
}
$workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.github/workflows/release.yml') -Raw
foreach ($block in [regex]::Matches($workflow, '(?ms)^\s{6,}- name:.*?(?=^\s{6,}- |^\s{2}[a-z]+:|\z)')) {
    $run = [regex]::Match($block.Value, '(?ms)^\s+run:.*')
    if ($run.Success -and $run.Value.Contains('${{')) { throw 'GitHub expressions must not enter executable script text.' }
}
foreach ($action in [regex]::Matches($workflow, 'uses:\s+actions/[^@\s]+@([^\s]+)')) {
    if ($action.Groups[1].Value -cnotmatch '^[0-9a-f]{40}$') { throw 'Every Action must use a full commit SHA.' }
}
foreach ($value in @('1.0.0; Write-Output injected', "1.0.0`nWrite-Output injected", '$(Write-Output injected)', "1.0.0'", '../1.0.0', '01.0.0')) {
    $rejected = $false
    try { $null = Get-AdapterReleaseVersion $value } catch { $rejected = $true }
    if (-not $rejected) { throw 'An unsafe release version was accepted.' }
    $previousValue = $env:MP_RELEASE_VERSION
    $previousEventName = $env:MP_RELEASE_EVENT
    try {
        $env:MP_RELEASE_EVENT = 'workflow_dispatch'
        $env:MP_RELEASE_VERSION = $value
        $rejected = $false
        try { $null = & (Join-Path $PSScriptRoot 'pack-adapter.ps1') } catch { $rejected = $true }
        if (-not $rejected) { throw 'The pack entry point accepted an unsafe release input.' }
    } finally { $env:MP_RELEASE_VERSION = $previousValue; $env:MP_RELEASE_EVENT = $previousEventName }
}
if ((Get-AdapterReleaseVersion '1.2.3') -cne '1.2.3') { throw 'The valid release version changed.' }
$previous = $env:MP_RELEASE_VERSION
$previousEvent = $env:MP_RELEASE_EVENT
try {
    $env:MP_RELEASE_EVENT = 'workflow_dispatch'
    $env:MP_RELEASE_VERSION = '0.1.0-preview.1'
    $output = @(& (Join-Path $PSScriptRoot 'pack-adapter.ps1'))
    $package = [string]$output[-1]
    $expectedIdentity = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-manifest.json') -Raw | ConvertFrom-Json).adapterId
    if ([IO.Path]::GetFileName($package) -cne "$expectedIdentity-0.1.0-preview.1.mpadapter") { throw 'The preview package filename differs from its identity.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.version -cne $env:MP_RELEASE_VERSION) { throw 'The package manifest version differs from its filename.' }
    } finally { $zip.Dispose() }
    Assert-AdapterPackageIdentity -PackagePath $package -ExpectedVersion $env:MP_RELEASE_VERSION
    & (Join-Path $PSScriptRoot 'sign-adapter.ps1') -PackagePath $package -DryRun
    foreach ($wrongIdentity in @('0.1.0', '0.1.0-preview.2')) {
        $rejected = $false
        try { Assert-AdapterPackageIdentity -PackagePath $package -ExpectedVersion $wrongIdentity } catch { $rejected = $true }
        if (-not $rejected) { throw 'A mismatched release identity was accepted.' }
    }
} finally { $env:MP_RELEASE_VERSION = $previous; $env:MP_RELEASE_EVENT = $previousEvent }
