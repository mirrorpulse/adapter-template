[CmdletBinding()]
param([string]$LocalCandidateDirectory)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$generated = Join-Path $repository ('artifacts/generated/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $generated -Force | Out-Null
$files = @(git -C $repository ls-files)
if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw 'Could not enumerate tracked template files.' }
. (Join-Path $PSScriptRoot 'release-policy.ps1')
foreach ($file in $files) {
    Assert-AdapterPackagePath $file
    $destination = Join-Path $generated $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $file) -Destination $destination
}
git -C $generated init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the generated template repository.' }

Push-Location $generated
try {
    $output = @(& ./Build-Adapter.ps1)
    $package = [string]$output[-1]
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw 'The generated template did not create a package.' }
    git check-ignore -- $package | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Generated Adapter packages must be ignored by Git.' }
    & ./eng/verify-template-worker.ps1 -PackagePath $package -LocalCandidateDirectory $LocalCandidateDirectory
    [ordered]@{ schemaVersion=1; verified=$true; packageSha256=(Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant() } |
        ConvertTo-Json | Set-Content -LiteralPath 'artifacts/template-conformance.json'
}
finally { Pop-Location }
