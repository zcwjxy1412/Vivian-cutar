[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ExePath)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('vivian-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$previous = $env:VIVIAN_SMOKE_DATA
try {
    $env:VIVIAN_SMOKE_DATA = $root
    $process = Start-Process -FilePath $ExePath -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) {
        # Only the exact process created by this test is stopped.
        $process.Kill()
        throw "WPF smoke timed out. Evidence retained at $root"
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path (Join-Path $root 'smoke.ok'))) {
        # Artifacts are not always reachable; print the end of the application's own log into the CI output.
        Get-ChildItem -Path $root -Recurse -Filter '*.log' -ErrorAction SilentlyContinue |
            ForEach-Object { Write-Output "----- $($_.Name) (last 40 lines)"; Get-Content $_.FullName -Tail 40 }
        throw "WPF smoke failed (exit code $($process.ExitCode)). Evidence retained at $root"
    }
    Get-Content (Join-Path $root 'smoke.ok')
    Write-Output "Smoke evidence: $root"
 } finally {
    $env:VIVIAN_SMOKE_DATA = $previous
    $evidence = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/smoke'
    New-Item -ItemType Directory -Force $evidence | Out-Null
    Copy-Item -Path (Join-Path $root '*') -Destination $evidence -Recurse -Force
}
