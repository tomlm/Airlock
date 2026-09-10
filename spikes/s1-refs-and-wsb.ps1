# Spike S1/S3/S4/S8/S9 — one sandbox session, exit-code probes only.
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$results = [ordered]@{}
function Note($k, $v) { $results[$k] = $v; Write-Host ("  {0,-34} {1}" -f $k, $v) }

$refsPath = 'S:\github\Airlock'
$ntfsPath = 'C:\Program Files\dotnet'
$stage    = Join-Path $env:TEMP 'airlock-spike'
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$xml = @"
<Configuration>
  <vGPU>Disable</vGPU>
  <Networking>Enable</Networking>
  <MappedFolders>
    <MappedFolder><HostFolder>$refsPath</HostFolder><SandboxFolder>C:\work\Airlock</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$ntfsPath</HostFolder><SandboxFolder>C:\airlock\dotnet</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  </MappedFolders>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MemoryInMB>4096</MemoryInMB>
</Configuration>
"@
# wsb --config takes INLINE XML only; file paths are rejected. Collapse to one line.
$cfg = ($xml -split "`n" | ForEach-Object { $_.Trim() }) -join ''

Write-Host "`n=== volumes ==="
Get-Volume | Where-Object DriveLetter -in 'S','C' | Select-Object DriveLetter,FileSystem | Format-Table -AutoSize

# --- S3: is --id honored? Try a caller-chosen GUID. ---
$myId = [guid]::NewGuid().ToString()
Write-Host "`n=== S3: wsb start --id $myId ==="
$sw = [Diagnostics.Stopwatch]::StartNew()
$startOut = & wsb start --id $myId --config $cfg --raw 2>&1 | Out-String
$sw.Stop()
Note 'start.blockingSeconds' ([math]::Round($sw.Elapsed.TotalSeconds,1))
Note 'start.exitCode' $LASTEXITCODE
Write-Host "start output: $startOut"

$listRaw = & wsb list --raw 2>&1 | Out-String
Write-Host "list --raw: $listRaw"
$ids = @()
try { $ids = (($listRaw | ConvertFrom-Json).WindowsSandboxEnvironments) } catch { }
Note 'list.count' $ids.Count
$id = if ($ids.Count -ge 1) { if ($ids[0].Id) { $ids[0].Id } else { $ids[0] } } else { $null }
Note 'list.firstId' $id
Note 'S3.idHonored' ($id -eq $myId)
if (-not $id) { throw 'no sandbox id found; aborting spike' }

try {
  # --- boot wait ---
  Write-Host "`n=== boot wait ==="
  $bootSw = [Diagnostics.Stopwatch]::StartNew()
  $booted = $false
  while ($bootSw.Elapsed.TotalSeconds -lt 600) {
    & wsb exec --id $id -r System -c "cmd.exe /c exit 0" *> $null
    if ($LASTEXITCODE -eq 0) { $booted = $true; break }
    Start-Sleep -Seconds 3
  }
  $bootSw.Stop()
  Note 'boot.seconds' ([math]::Round($bootSw.Elapsed.TotalSeconds,1))
  Note 'boot.ok' $booted
  if (-not $booted) { throw 'sandbox never became exec-ready' }

  # --- S4: does wsb exec propagate exit codes? ---
  Write-Host "`n=== S4: exec exit-code propagation ==="
  & wsb exec --id $id -r System -c "cmd.exe /c exit 7" *> $null
  Note 'S4.exitCode7' $LASTEXITCODE
  Note 'S4.propagatesExitCode' ($LASTEXITCODE -eq 7)

  # --- S4b: does wsb exec block until the command exits? ---
  $sw2 = [Diagnostics.Stopwatch]::StartNew()
  & wsb exec --id $id -r System -c "cmd.exe /c ping -n 11 127.0.0.1" *> $null
  $sw2.Stop()
  Note 'S4b.pingSeconds' ([math]::Round($sw2.Elapsed.TotalSeconds,1))
  Note 'S4b.blocks' ($sw2.Elapsed.TotalSeconds -gt 8)

  # --- S1: does a ReFS host folder map? (control: NTFS) ---
  Write-Host "`n=== S1: ReFS vs NTFS mapping ==="
  & wsb exec --id $id -r System -c "cmd.exe /c if exist C:\airlock\dotnet\dotnet.exe (exit 0) else (exit 1)" *> $null
  Note 'S1.ntfsControlMapped' ($LASTEXITCODE -eq 0)
  & wsb exec --id $id -r System -c "cmd.exe /c if exist C:\work\Airlock\LICENSE (exit 0) else (exit 1)" *> $null
  Note 'S1.refsProjectMapped' ($LASTEXITCODE -eq 0)
  & wsb exec --id $id -r System -c "cmd.exe /c if exist C:\work\Airlock (exit 0) else (exit 1)" *> $null
  Note 'S1.refsDirExists' ($LASTEXITCODE -eq 0)

  # --- S9: wsb share -w on a RUNNING sandbox ---
  Write-Host "`n=== S9: share -w on running sandbox ==="
  $outDir = Join-Path $stage 'out'
  Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  $shareOut = & wsb share --id $id -f $outDir -s 'C:\airlock\out' -w --raw 2>&1 | Out-String
  Note 'S9.shareExit' $LASTEXITCODE
  Write-Host "share output: $shareOut"
  & wsb exec --id $id -r System -c "cmd.exe /c echo hello-from-sandbox> C:\airlock\out\probe.txt" *> $null
  Note 'S9.writeExit' $LASTEXITCODE
  Start-Sleep -Seconds 2
  $probe = Join-Path $outDir 'probe.txt'
  Note 'S9.hostSeesFile' (Test-Path $probe)
  if (Test-Path $probe) { Note 'S9.content' ((Get-Content $probe -Raw).Trim()) }

  # --- S8: a second concurrent sandbox? ---
  Write-Host "`n=== S8: concurrent sandbox ==="
  $xml2 = '<Configuration><vGPU>Disable</vGPU><Networking>Enable</Networking><MemoryInMB>2048</MemoryInMB></Configuration>'
  $out2 = & wsb start --config $xml2 --raw 2>&1 | Out-String
  Note 'S8.secondStartExit' $LASTEXITCODE
  Write-Host "second start: $out2"
  $list2 = & wsb list --raw 2>&1 | Out-String
  $ids2 = @()
  try { $ids2 = (($list2 | ConvertFrom-Json).WindowsSandboxEnvironments) } catch { }
  Note 'S8.concurrentCount' $ids2.Count
  Note 'S8.multipleSupported' ($ids2.Count -ge 2)
}
finally {
  Write-Host "`n=== teardown ==="
  $listF = & wsb list --raw 2>&1 | Out-String
  $idsF = @()
  try { $idsF = (($listF | ConvertFrom-Json).WindowsSandboxEnvironments) } catch { }
  foreach ($e in $idsF) {
    $eid = if ($e.Id) { $e.Id } else { $e }
    Write-Host "  stopping $eid"
    & wsb stop --id $eid *> $null
  }
  Start-Sleep -Seconds 2
  $listZ = & wsb list --raw 2>&1 | Out-String
  Write-Host "  after stop: $listZ"
}

Write-Host "`n=== RESULTS ==="
$results.GetEnumerator() | ForEach-Object { "{0,-34} {1}" -f $_.Key, $_.Value }
$results | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $PSScriptRoot 's1-results.json')
