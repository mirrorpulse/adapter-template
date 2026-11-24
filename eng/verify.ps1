[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & ./eng/verify-adapter-version.ps1
    & ./eng/verify-adapter-publishing.ps1
    $settings = Get-Content -LiteralPath 'eng/release-settings.json' -Raw | ConvertFrom-Json
    $project = [string]$settings.workerProject
    dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'The Worker locked restore failed.' }
    dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'The Worker build failed.' }
    dotnet format $project --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw 'The Worker format check failed.' }
}
finally { Pop-Location }
