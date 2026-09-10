# Covers the three ways `airlock stop` can end up:
#   1. nothing running
#   2. a sandbox we can prove is ours          -> plain stop works
#   3. a sandbox we cannot prove is ours       -> plain stop refuses, --force clears it
# Case 3 is the dead end this was written for: an interrupted run can leave the VM alive with its
# session key gone, and before --force neither start nor stop could get out of it.
$ErrorActionPreference = 'Continue'

$exe = 'S:\github\Airlock\src\Airlock\bin\Debug\net10.0\airlock.exe'
$keyDir = "$env:LOCALAPPDATA\Airlock\session\key"
Set-Location 'S:\github\Airlock\spikes'

Write-Host "`n=== 1. stop with nothing running ==="
& $exe stop 2>&1 | Out-String | Write-Host

Write-Host "`n=== 2. start a session, then stop it normally ==="
& $exe -- powershell -Command "'up'" 2>&1 | Out-String | Write-Host
& $exe stop 2>&1 | Out-String | Write-Host
Write-Host "wsb after: $((wsb list --raw | Out-String).Trim() -replace '\s+',' ')"

Write-Host "`n=== 3. recreate the dead end: sandbox alive, session key destroyed ==="
& $exe -- powershell -Command "'up again'" 2>&1 | Out-String | Write-Host
$id = (Get-Content "$env:LOCALAPPDATA\Airlock\sandbox.json" -Raw | ConvertFrom-Json).Id
Write-Host "sandbox: $id"

# Exactly what an interrupted teardown leaves behind: the VM, but nothing to prove it is ours.
Remove-Item $keyDir -Recurse -Force
Remove-Item "$env:LOCALAPPDATA\Airlock\sandbox.json" -Force
Write-Host "key + state destroyed; sandbox still running: $((wsb list --raw | Out-String) -match $id)"

Write-Host "`n--- plain stop should refuse and point at --force ---"
& $exe stop 2>&1 | Out-String | Write-Host

Write-Host "`n--- start should also refuse (this is the trap) ---"
& $exe -- powershell -Command "'should not get here'" 2>&1 | Out-String | Write-Host

Write-Host "`n--- stop --force should clear it (stdin redirected, so no prompt) ---"
& $exe stop --force 2>&1 | Out-String | Write-Host
Write-Host "wsb after force: $((wsb list --raw | Out-String).Trim() -replace '\s+',' ')"

Write-Host "`n=== 4. airlock works again afterwards ==="
& $exe stop 2>&1 | Out-String | Write-Host
