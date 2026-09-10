# The GUI pivot, end to end:
#   1. start mounts every configured tool and airlock, provisions, and shows the desktop
#   2. tools are on PATH inside, and env came through
#   3. airlocks are writable, Airlock's own folders are not
#   4. open launches a tool in the desktop, in the right folder
#   5. an unconfigured folder is auto-added
#   6. ownership survives losing the state file
$ErrorActionPreference = 'Continue'

$exe   = 'S:\github\Airlock\src\Airlock\bin\Debug\net10.0\airlock.exe'
$out   = "$env:LOCALAPPDATA\Airlock\session\out"
$state = "$env:LOCALAPPDATA\Airlock\sandbox.json"
Set-Location 'S:\github\Airlock'

function Ask($label, $ps) {
    # wsb exec says nothing back, so the guest answers through the shared folder.
    $probe = Join-Path $out 'answer.txt'
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
    $id = (Get-Content $state -Raw | ConvertFrom-Json).Id
    # Assign first: `try {} catch {} | Out-File` is a parse error, so piping the probe directly
    # silently produced no answer at all.
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(
        "`$r = $ps
`$r | Out-File -Encoding utf8 'C:\airlock\_out_\answer.txt'"))
    & wsb exec --id $id -r System -c "powershell.exe -NoProfile -EncodedCommand $enc" *> $null
    # An empty answer is a real result (a null property), not a missing one - Trim() on it threw.
    $value = if (Test-Path $probe) {
        $c = Get-Content $probe -Raw
        if ([string]::IsNullOrWhiteSpace($c)) { '(empty)' } else { $c.Trim() }
    } else { '(no answer)' }
    Write-Host ("  {0,-22} {1}" -f $label, $value)
}

Write-Host "`n=== configured before start ==="
& $exe list 2>&1 | Out-String | Write-Host

Write-Host "`n=== 1. start ==="
$sw = [Diagnostics.Stopwatch]::StartNew()
& $exe start 2>&1 | Out-String | Write-Host
$sw.Stop()
Write-Host "start took $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
Write-Host "desktop window present: $([bool](Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue))"

Write-Host "`n=== 2. tools on PATH, env applied ==="
Ask 'dotnet'      '(Get-Command dotnet -EA SilentlyContinue).Source'
Ask 'git'         '(Get-Command git -EA SilentlyContinue).Source'
Ask 'node'        '(Get-Command node -EA SilentlyContinue).Source'
Ask 'python'      '(Get-Command python -EA SilentlyContinue).Source'
Ask 'ls (coreutils)' '(Get-Command ls.exe -EA SilentlyContinue).Source'
# ~80 hardlinks to one 9MB binary. If Sandbox materialised them instead of passing the links
# through, this folder would cost 700MB rather than 9MB - worth knowing which happens.
Ask 'ls runs'     '(& ls.exe --version 2>&1 | Select-Object -First 1)'
Ask 'coreutils MB' '[math]::Round(((Get-ChildItem C:\airlock\_tools_\coreutils -File | Measure-Object -Sum Length).Sum/1MB))'
# Empty means the mount does not surface hardlink metadata - which is fine, since each entry still
# reads as a whole binary, and that is what makes bare `ls` work.
Ask 'ls.exe linktype' '(Get-Item C:\airlock\_tools_\coreutils\ls.exe).LinkType'
Ask 'ls.exe MB'       '[math]::Round((Get-Item C:\airlock\_tools_\coreutils\ls.exe).Length/1MB,1)'
Ask 'DOTNET_ROOT' '[Environment]::GetEnvironmentVariable("DOTNET_ROOT","Machine")'
Ask 'AIRLOCK'     '[Environment]::GetEnvironmentVariable("AIRLOCK","Machine")'
Ask 'api key set' '[bool][Environment]::GetEnvironmentVariable("ANTHROPIC_API_KEY","Machine")'

Write-Host "`n=== 3. what is writable ==="
Ask 'airlock rw'  'try { Set-Content C:\airlock\Airlock\_probe.txt hi -EA Stop; "WRITABLE" } catch { "DENIED" }'
# _tools_ itself is sandbox-local scratch that holds the mount points; the per-tool folders under it
# are the actual read-only host mounts, so that is what the invariant is about.
Ask 'tools container' 'try { Set-Content C:\airlock\_tools_\_probe.txt hi -EA Stop; "writable (sandbox-local)" } catch { "DENIED" }'
Ask 'tool mount ro'   'try { Set-Content C:\airlock\_tools_\dotnet\_probe.txt hi -EA Stop; "WRITABLE" } catch { "DENIED" }'
Ask 'session ro'  'try { Set-Content C:\airlock\_session_\_probe.txt hi -EA Stop; "WRITABLE" } catch { "DENIED" }'
Ask 'contents'    '(Get-ChildItem C:\airlock -Directory | Select-Object -Expand Name) -join ","'
Ask 'secret file' 'Test-Path C:\airlock\_out_\secrets.json'

Write-Host "`n=== 4. open a tool in the desktop ==="
& $exe open 2>&1 | Out-String | Write-Host
Start-Sleep -Seconds 3
Ask 'cmd windows'  '@(Get-Process cmd -EA SilentlyContinue).Count'

Write-Host "`n=== 5. an unconfigured folder is auto-added ==="
Set-Location 'S:\github\Airlock\spikes'
& $exe open 2>&1 | Out-String | Write-Host
Set-Location 'S:\github\Airlock'
& $exe list 2>&1 | Out-String | Write-Host

Write-Host "`n=== 6. ownership survives losing the record ==="
$before = (Get-Content $state -Raw | ConvertFrom-Json).Id
Remove-Item $state -Force
& $exe list 2>&1 | Select-Object -First 3 | Out-String | Write-Host
$after = if (Test-Path $state) { (Get-Content $state -Raw | ConvertFrom-Json).Id } else { '(none)' }
Write-Host "ADOPTED CORRECTLY: $($before -eq $after)"

Write-Host "`n=== teardown ==="
Remove-Item 'S:\github\Airlock\_probe.txt' -Force -ErrorAction SilentlyContinue
& $exe stop 2>&1 | Out-String | Write-Host
Write-Host "wsb list: $((wsb list --raw | Out-String).Trim() -replace '\s+',' ')"
Write-Host "secrets left on host: $(Test-Path "$out\secrets.json")"
