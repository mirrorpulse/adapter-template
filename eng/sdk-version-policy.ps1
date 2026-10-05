function Get-SdkReleaseVersion {
    param([AllowNull()][string]$Value)
    if (-not $Value -or $Value.Length -gt 64 -or
        $Value -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview\.([1-9][0-9]*))?\z') {
        throw 'SDK versions must be canonical X.Y.Z or X.Y.Z-preview.N.'
    }
    $parsed = $null
    if (-not [Version]::TryParse(($Value -split '-')[0], [ref]$parsed)) { throw 'The SDK base version is outside the supported range.' }
    return $Value
}
