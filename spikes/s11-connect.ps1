# Verifies `airlock connect`: that the window opens, that the call returns instead of blocking,
# and which Windows account the desktop session actually runs as.
#
# NB: ErrorActionPreference stays at Continue. wsb writes to stderr on the ExistingLogin call we
# *expect* to fail, and under 'Stop' PowerShell turns that into a terminating NativeCommandError.
$ErrorActionPreference = 'Continue'

$exe = 'S:\github\Airlock\src\Airlock\bin\Debug\net10.0\airlock.exe'
$probeDir = 'S:\github\Airlock\spikes'
$probe = Join-Path $probeDir '_who.txt'
Remove-Item $probe -Force -ErrorAction SilentlyContinue

function Try-ExistingLogin($id) {
    & wsb exec --id $id -r ExistingLogin -c "cmd.exe /c whoami > C:\airlock\spikes\_who.txt" 2>&1 | Out-Null
    return (Test-Path $probe)
}

Write-Host "`n=== ensure a sandbox with a writable folder attached ==="
Set-Location $probeDir
& $exe open powershell -Command "'attached' | Write-Output" 2>&1 | Out-String | Write-Host

$id = (Get-Content "$env:LOCALAPPDATA\Airlock\sandbox.json" -Raw | ConvertFrom-Json).Id
Write-Host "sandbox: $id"

Write-Host "`n=== ExistingLogin BEFORE connect (expected to fail: no client attached) ==="
Write-Host "worked: $(Try-ExistingLogin $id)"

Write-Host "`n=== airlock connect (a window should appear) ==="
$sw = [Diagnostics.Stopwatch]::StartNew()
& $exe connect 2>&1 | Out-String | Write-Host
$sw.Stop()
Write-Host "airlock connect returned after $([math]::Round($sw.Elapsed.TotalSeconds,1))s (seconds = did not block on the window)"

Write-Host "`n=== wait for the desktop session to sign in ==="
$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    if (Try-ExistingLogin $id) { $ok = $true; break }
}
Write-Host "ExistingLogin worked after connect: $ok"
if ($ok) { Write-Host "desktop session runs as: $((Get-Content $probe -Raw).Trim())" }

Write-Host "`n=== teardown ==="
Remove-Item $probe -Force -ErrorAction SilentlyContinue
& $exe stop 2>&1 | Out-String | Write-Host
Write-Host "wsb list: $((wsb list --raw | Out-String).Trim())"
