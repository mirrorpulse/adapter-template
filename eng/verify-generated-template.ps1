[CmdletBinding()]
param([string]$LocalCandidateDirectory, [string]$ProductRepositoryPath, [switch]$CheckReleasePolicy)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$product = if ($ProductRepositoryPath) { (Resolve-Path -LiteralPath $ProductRepositoryPath).Path }
$runId = [guid]::NewGuid().ToString('N')
$evidence = Join-Path $repository ('artifacts/generated/' + $runId)
# Keep MSBuild's native apphost paths short even when the template checkout is deeply nested.
$temporaryRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path)
$generated = Join-Path $temporaryRoot ('mp-template-' + $runId)
New-Item -ItemType Directory -Path $generated, $evidence | Out-Null
$workingDirectory = (Resolve-Path -LiteralPath $generated).Path
if ([IO.Path]::GetDirectoryName($workingDirectory) -ne $temporaryRoot) {
    throw 'The generated repository is outside the temporary directory.'
}

try {
    $files = @(git -C $repository ls-files)
    if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw 'Could not enumerate tracked template files.' }
    . (Join-Path $PSScriptRoot 'release-policy.ps1')
    foreach ($file in $files) {
        Assert-AdapterPackagePath $file
        $destination = Join-Path $workingDirectory $file
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repository $file) -Destination $destination
    }
    git -C $workingDirectory init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the generated template repository.' }

    Push-Location -LiteralPath $workingDirectory
    try {
        $buildArgs = @{}
        if ($CheckReleasePolicy) { $buildArgs.Version = '0.1.0-preview.1' }
        $output = @(& ./Build-Adapter.ps1 @buildArgs)
        $package = [string]$output[-1]
        if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw 'The generated template did not create a package.' }
        git check-ignore -- $package | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Generated Adapter packages must be ignored by Git.' }
        if ($CheckReleasePolicy) { & ./eng/verify-release.ps1 -SignedPreviewPackagePath $package }
        & ./eng/verify-template-worker.ps1 -PackagePath $package -LocalCandidateDirectory $LocalCandidateDirectory
        if ($product) {
            & ./eng/verify-template-product.ps1 -PackagePath $package -ProductRepositoryPath $product `
                -ResultsDirectory (Join-Path $evidence 'product-host')
        }
        [ordered]@{
            schemaVersion = 1
            verified = $true
            releasePolicyVerified = $CheckReleasePolicy.IsPresent
            productHostVerified = [bool]$product
            packageSha256 = (Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant()
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'template-conformance.json')
    }
    finally { Pop-Location }
}
finally {
    $cleanupPath = (Resolve-Path -LiteralPath $workingDirectory).Path
    if ($cleanupPath -eq $workingDirectory -and [IO.Path]::GetDirectoryName($cleanupPath) -eq $temporaryRoot) {
        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
    }
}
