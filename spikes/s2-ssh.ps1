# Spike S2 (OpenSSH key auth) + S5 (SendEnv/AcceptEnv) + writability invariant.
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
$results = [ordered]@{}
function Note($k,$v){ $results[$k]=$v; Write-Host ("  {0,-30} {1}" -f $k,$v) }

$sshExe    = 'C:\Program Files\OpenSSH\ssh.exe'
$keygenExe = 'C:\Program Files\OpenSSH\ssh-keygen.exe'
$tools     = "$env:LOCALAPPDATA\Airlock\tools"
$project   = 'S:\github\Airlock'
$sess      = Join-Path $env:TEMP ('airlock-s2-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$share     = Join-Path $sess 'share'
$keyDir    = Join-Path $sess 'key'
$outDir    = Join-Path $sess 'out'
$null = New-Item -ItemType Directory -Force -Path $share,$keyDir,$outDir

# --- keypair (the private key NEVER goes into $share) ---
$key = Join-Path $keyDir 'id_ed25519'
# Empty -N through PowerShell is unreliable; go via cmd.exe so the empty arg survives.
& cmd.exe /c "`"$keygenExe`" -t ed25519 -N `"`" -C airlock-session -f `"$key`" -q"
if (-not (Test-Path $key)) { throw 'ssh-keygen produced no key' }
if ((Get-Content $key -Raw) -match 'ENCRYPTED') { throw 'key has a passphrase; -N did not survive' }
& icacls.exe $key /inheritance:r /grant "$($env:USERNAME):F" | Out-Null
Copy-Item "$key.pub" (Join-Path $share 'authorized_key.pub') -Force
Note 'keygen.ok' $true

# --- setup.ps1 with tokens substituted ---
$pathPrepend = 'C:\airlock\dotnet;C:\airlock\tools\OpenSSH-Win64'
$setup = Get-Content (Join-Path $PSScriptRoot 'setup.ps1') -Raw
$setup = $setup.Replace('{{SANDBOX_USER}}','airlock')
$setup = $setup.Replace('{{ACCEPT_ENV}}','AIRLOCK_SECRET TERM COLORTERM')
$setup = $setup.Replace('{{PATH_PREPEND}}',$pathPrepend)
$setup = $setup.Replace('{{DEFAULT_SHELL}}','C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe')
Set-Content -LiteralPath (Join-Path $share 'setup.ps1') -Value $setup -Encoding UTF8
@{ DOTNET_ROOT='C:\airlock\dotnet'; DOTNET_NOLOGO='1'; AIRLOCK='1' } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $share 'env.json') -Encoding UTF8

# --- inline single-line config ---
$cfg = "<Configuration><vGPU>Disable</vGPU><Networking>Enable</Networking><MappedFolders>" +
  "<MappedFolder><HostFolder>$project</HostFolder><SandboxFolder>C:\work\Airlock</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>" +
  "<MappedFolder><HostFolder>$tools</HostFolder><SandboxFolder>C:\airlock\tools</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" +
  "<MappedFolder><HostFolder>C:\Program Files\dotnet</HostFolder><SandboxFolder>C:\airlock\dotnet</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" +
  "<MappedFolder><HostFolder>$share</HostFolder><SandboxFolder>C:\airlock\session</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" +
  "</MappedFolders><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput>" +
  "<PrinterRedirection>Disable</PrinterRedirection><ClipboardRedirection>Disable</ClipboardRedirection>" +
  "<MemoryInMB>6144</MemoryInMB></Configuration>"

$id = [guid]::NewGuid().ToString()
Write-Host "`n=== start $id ==="
& wsb start --id $id --config $cfg --raw | Out-String | Write-Host
if ($LASTEXITCODE -ne 0) { throw "wsb start failed: $LASTEXITCODE" }

try {
  Write-Host "`n=== boot wait ==="
  $sw = [Diagnostics.Stopwatch]::StartNew(); $ok = $false
  while ($sw.Elapsed.TotalSeconds -lt 600) {
    & wsb exec --id $id -r System -c "cmd.exe /c exit 0" *> $null
    if ($LASTEXITCODE -eq 0) { $ok = $true; break }
    Start-Sleep 3
  }
  Note 'boot.seconds' ([math]::Round($sw.Elapsed.TotalSeconds,1)); Note 'boot.ok' $ok
  if (-not $ok) { throw 'never booted' }

  Write-Host "`n=== provision (wsb exec blocks) ==="
  $sw2 = [Diagnostics.Stopwatch]::StartNew()
  & wsb exec --id $id -r System -c "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\airlock\session\setup.ps1" *> $null
  $sw2.Stop(); Note 'provision.seconds' ([math]::Round($sw2.Elapsed.TotalSeconds,1))

  Write-Host "`n=== ip ==="
  $ipRaw = & wsb ip --id $id --raw | Out-String
  Write-Host $ipRaw
  $ip = $null
  if ($ipRaw -match '(\d+\.\d+\.\d+\.\d+)') { $ip = $Matches[1] }
  Note 'ip' $ip
  if (-not $ip) { throw 'no ip' }

  Write-Host "`n=== wait for tcp 22 ==="
  $sw3 = [Diagnostics.Stopwatch]::StartNew(); $open = $false
  while ($sw3.Elapsed.TotalSeconds -lt 180) {
    try { $c = New-Object Net.Sockets.TcpClient; $c.Connect($ip,22); $open = $c.Connected; $c.Close() } catch {}
    if ($open) { break }
    Start-Sleep 2
  }
  Note 'tcp22.seconds' ([math]::Round($sw3.Elapsed.TotalSeconds,1)); Note 'tcp22.open' $open

  $sshArgs = @('-i',$key,'-o','IdentitiesOnly=yes','-o','IdentityAgent=none',
               '-o','PreferredAuthentications=publickey','-o','BatchMode=yes',
               '-o','StrictHostKeyChecking=no','-o','UserKnownHostsFile=NUL',
               '-o','GlobalKnownHostsFile=NUL','-o','LogLevel=ERROR','-o','ConnectTimeout=10')

  if ($open) {
    Write-Host "`n=== S2: key auth ==="
    $whoami = & $sshExe @sshArgs "airlock@$ip" 'whoami' 2>&1 | Out-String
    Note 'S2.sshExit' $LASTEXITCODE
    Note 'S2.whoami' ($whoami.Trim())
    Note 'S2.keyAuthWorks' ($whoami.Trim() -match 'airlock')

    Write-Host "`n=== S5: SendEnv / AcceptEnv ==="
    $env:AIRLOCK_SECRET = 'sup3r-s3cret-value'
    $sec = & $sshExe @sshArgs '-o' 'SendEnv=AIRLOCK_SECRET' "airlock@$ip" '$env:AIRLOCK_SECRET' 2>&1 | Out-String
    Note 'S5.received' ($sec.Trim())
    Note 'S5.sendEnvWorks' ($sec.Trim() -eq 'sup3r-s3cret-value')
    Remove-Item Env:AIRLOCK_SECRET -ErrorAction SilentlyContinue

    Write-Host "`n=== machine env + PATH ==="
    Note 'env.DOTNET_ROOT' ((& $sshExe @sshArgs "airlock@$ip" '$env:DOTNET_ROOT' 2>&1 | Out-String).Trim())
    Note 'env.dotnetVersion' ((& $sshExe @sshArgs "airlock@$ip" 'dotnet --version' 2>&1 | Out-String).Trim())

    Write-Host "`n=== writability invariant ==="
    # A raw PowerShell one-liner as the ssh remote command gets shredded by the quoting layers
    # (host argv -> ssh remote-command concatenation -> DefaultShell -Command). -EncodedCommand
    # collapses all of them, and is what the product uses.
    function Remote($ps) {
        $b = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($ps))
        (& $sshExe @sshArgs "airlock@$ip" 'powershell.exe' '-NoLogo' '-NoProfile' '-EncodedCommand' $b 2>&1 | Out-String).Trim()
    }
    function Probe($path) {
        Remote ('try { Set-Content -LiteralPath ''' + $path + '\_probe.txt'' -Value hi -EA Stop; "WRITABLE" } catch { "DENIED" }')
    }
    Note 'inv.project' (Probe 'C:\work\Airlock')
    Note 'inv.tools'   (Probe 'C:\airlock\tools')
    Note 'inv.session' (Probe 'C:\airlock\session')
    Note 'inv.dotnet'  (Probe 'C:\airlock\dotnet')

    Write-Host "`n=== network posture (sandbox is on 172.x - inside RFC1918) ==="
    Note 'net.sandboxIp' $ip
    Note 'net.gateway'  (Remote '(Get-NetRoute -DestinationPrefix "0.0.0.0/0" | Select-Object -First 1).NextHop')
    Note 'net.internet' (Remote 'try { $null = Invoke-WebRequest https://api.anthropic.com -UseBasicParsing -TimeoutSec 20; "OK" } catch { if ($_.Exception.Response) { "OK-http" } else { "FAIL " + $_.Exception.Message } }')
    Note 'net.hostReachable' (Remote 'if (Test-Connection -ComputerName 172.22.144.1 -Count 1 -Quiet) { "REACHABLE" } else { "no" }')

    Write-Host "`n=== -EncodedCommand round trip ==="
    $prologue = "Set-Location -LiteralPath 'C:\work\Airlock'; (Get-Location).Path"
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($prologue))
    Note 'encodedCommand.cwd' ((& $sshExe @sshArgs "airlock@$ip" 'powershell.exe' '-NoLogo' '-NoProfile' '-EncodedCommand' $b64 2>&1 | Out-String).Trim())
  }

  if (-not $open -or -not $results['S2.keyAuthWorks']) {
    Write-Host "`n=== failure diagnostics via share -w ==="
    & wsb share --id $id -f $outDir -s 'C:\airlock\out' -w *> $null
    & wsb exec --id $id -r System -c "cmd.exe /c copy C:\airlock\setup.log C:\airlock\out\ & copy C:\airlock\ready.json C:\airlock\out\" *> $null
    Start-Sleep 3
    foreach ($f in 'ready.json','setup.log') {
      $p = Join-Path $outDir $f
      if (Test-Path $p) { Write-Host "--- $f ---"; Get-Content $p -Raw | Write-Host }
      else { Write-Host "--- $f MISSING ---" }
    }
  }
}
finally {
  Write-Host "`n=== teardown ==="
  Remove-Item (Join-Path $project '_probe.txt') -Force -ErrorAction SilentlyContinue
  & wsb stop --id $id *> $null
  Remove-Item $sess -Recurse -Force -ErrorAction SilentlyContinue
  Note 'teardown.sessionDirGone' (-not (Test-Path $sess))
}

Write-Host "`n=== RESULTS ==="
$results.GetEnumerator() | ForEach-Object { "{0,-30} {1}" -f $_.Key,$_.Value }
