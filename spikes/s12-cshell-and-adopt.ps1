# Covers three things at once:
#  1. the TTY hand-off via CShell.Run with all three streams unredirected (exit code must survive)
#  2. recovering ownership of a running sandbox after the local state file is destroyed
#  3. `wsb connect` launched through CShell.Start
$ErrorActionPreference = 'Continue'

$exe = 'S:\github\Airlock\src\Airlock\bin\Debug\net10.0\airlock.exe'
$stateFile = "$env:LOCALAPPDATA\Airlock\sandbox.json"
Set-Location 'S:\github\Airlock\spikes'

Write-Host "`n=== 1. session via unredirected CShell.Run ==="
& $exe open powershell -Command "'hello-from-sandbox'" 2>&1 | Out-String | Write-Host
Write-Host "exit code passthrough (expect 0): $LASTEXITCODE"

Write-Host "`n=== 1b. a non-zero remote exit code must come back ==="
& $exe open powershell -Command "exit 42" 2>&1 | Out-String | Write-Host
Write-Host "exit code passthrough (expect 42): $LASTEXITCODE"

Write-Host "`n=== 2. destroy the local record, then see if airlock recognises its own sandbox ==="
$before = (Get-Content $stateFile -Raw | ConvertFrom-Json).Id
Write-Host "sandbox before: $before"
Remove-Item $stateFile -Force
Write-Host "state file deleted: $(-not (Test-Path $stateFile))"

& $exe list 2>&1 | Out-String | Write-Host

$after = if (Test-Path $stateFile) { (Get-Content $stateFile -Raw | ConvertFrom-Json).Id } else { '(none)' }
Write-Host "sandbox after adoption: $after"
Write-Host "ADOPTED CORRECTLY: $($before -eq $after)"

Write-Host "`n=== 3. connect via CShell.Start ==="
$sw = [Diagnostics.Stopwatch]::StartNew()
& $exe connect 2>&1 | Out-String | Write-Host
$sw.Stop()
Write-Host "connect returned after $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
Start-Sleep -Seconds 5
Write-Host "sandbox window present: $([bool](Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue))"

Write-Host "`n=== teardown ==="
& $exe stop 2>&1 | Out-String | Write-Host
Write-Host "wsb list: $((wsb list --raw | Out-String).Trim())"
