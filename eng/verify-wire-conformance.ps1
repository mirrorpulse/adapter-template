[CmdletBinding()]
param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$pythonPath = (Get-Command $Python).Source
$fixture = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../tests/wire-worker.py'))
$runner = Join-Path $PSScriptRoot '../tools/MirrorPulse.Adapter.Conformance/bin/Release/net10.0-windows/MirrorPulse.Adapter.Conformance.exe'
$root = Join-Path ([IO.Path]::GetFullPath('artifacts/wire-conformance')) ([Guid]::NewGuid().ToString('N'))
$previous = $env:MP_WIRE_FIXTURE_FAULT
try {
    foreach ($fault in @('', 'root', 'capability', 'cancel')) {
        $env:MP_WIRE_FIXTURE_FAULT = $fault
        $cache = Join-Path $root $(if ($fault) { $fault } else { 'valid' })
        $output = @(& $runner --worker $pythonPath --transfer-cache $cache --worker-argument $fixture 2>&1)
        $code = $LASTEXITCODE
        if (($fault -eq '' -and $code -ne 0) -or ($fault -ne '' -and $code -eq 0)) { throw 'Wire-only conformance did not detect the expected boundary.' }
        if ($fault -ne '') {
            $expected = switch ($fault) { 'root' { 'Response root correlation' }; 'capability' { 'V2 negotiation' }; 'cancel' { 'Response root correlation' } }
            if (($output -join "`n") -notlike "*$expected*") { throw 'Negative fixture failed for an unrelated reason.' }
        }
    }
} finally { $env:MP_WIRE_FIXTURE_FAULT = $previous }
Write-Host 'SDK-free wire process passed; wrong roots, missing capabilities and broken cancellation were rejected.'
