#requires -Version 5.1
# Airlock in-guest provisioning. Runs as SYSTEM via `wsb exec -r System`.
# Tokens: {{SANDBOX_USER}} {{ACCEPT_ENV}} {{PATH_PREPEND}} {{DEFAULT_SHELL}}
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

New-Item -ItemType Directory -Force -Path 'C:\airlock' | Out-Null
Start-Transcript -Path 'C:\airlock\setup.log' -Force | Out-Null
$phase = 'start'
function Step($n) { $script:phase = $n; Write-Host "== AIRLOCK STEP: $n" }

try {
  Step 'copy-openssh'
  $null = robocopy 'C:\airlock\tools\OpenSSH-Win64' 'C:\OpenSSH' /E /R:1 /W:1 /NFL /NDL /NJH /NJS
  if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }
  $global:LASTEXITCODE = 0

  Step 'install-sshd'
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\OpenSSH\install-sshd.ps1'
  & 'C:\OpenSSH\ssh-keygen.exe' -A

  Step 'firewall'
  New-NetFirewallRule -Name 'airlock-sshd' -DisplayName 'Airlock sshd' -Enabled True `
      -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Any | Out-Null

  Step 'user'
  $bytes = New-Object byte[] 32
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  $pwPlain = [Convert]::ToBase64String($bytes) + '!aA9'
  $pw = ConvertTo-SecureString $pwPlain -AsPlainText -Force
  New-LocalUser -Name '{{SANDBOX_USER}}' -Password $pw -AccountNeverExpires `
      -PasswordNeverExpires -UserMayNotChangePassword -Description 'Airlock session user' | Out-Null
  # Administrators resolved by well-known SID so this is locale-independent.
  $admins = (Get-LocalGroup -SID 'S-1-5-32-544').Name
  Add-LocalGroupMember -Group $admins -Member '{{SANDBOX_USER}}'
  # Force the profile to exist before first login.
  $cred = New-Object pscredential('{{SANDBOX_USER}}', $pw)
  Start-Process -FilePath 'cmd.exe' -ArgumentList '/c','exit' -Credential $cred `
      -LoadUserProfile -Wait -ErrorAction SilentlyContinue

  Step 'authorized-keys'
  $sshData = 'C:\ProgramData\ssh'
  New-Item -ItemType Directory -Force -Path $sshData | Out-Null
  $aak = Join-Path $sshData 'administrators_authorized_keys'
  Copy-Item 'C:\airlock\session\authorized_key.pub' $aak -Force
  # OpenSSH SILENTLY ignores this file unless the ACL is exactly Administrators + SYSTEM.
  & icacls.exe $aak /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "icacls failed: $LASTEXITCODE" }

  Step 'sshd-config'
  $cfg = Join-Path $sshData 'sshd_config'
  if (-not (Test-Path $cfg)) { Copy-Item 'C:\OpenSSH\sshd_config_default' $cfg -Force }
  (Get-Content $cfg) -replace '^\s*#?\s*(PasswordAuthentication|PubkeyAuthentication|AcceptEnv)\b', '#disabled-$1' |
      Set-Content $cfg
  Add-Content $cfg ("`r`n# --- Airlock ---`r`nPubkeyAuthentication yes`r`nPasswordAuthentication no`r`nPermitEmptyPasswords no`r`nAcceptEnv {{ACCEPT_ENV}}`r`nClientAliveInterval 30`r`nClientAliveCountMax 10")

  Step 'default-shell'
  New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
  Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name 'DefaultShell' -Type String -Value '{{DEFAULT_SHELL}}'
  Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name 'DefaultShellCommandOption' -Type String -Value '-Command'
  Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name 'DefaultShellEscapeArguments' -Type DWord -Value 0

  Step 'machine-env'
  $envKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  # Read Path WITHOUT expanding, or we bake in literals and demote REG_EXPAND_SZ to REG_SZ.
  $rawPath = (Get-Item -LiteralPath $envKey).GetValue('Path', '', 'DoNotExpandEnvironmentNames')
  Set-ItemProperty -LiteralPath $envKey -Name 'Path' -Value ('{{PATH_PREPEND}}' + ';' + $rawPath) -Type ExpandString
  $envFile = 'C:\airlock\session\env.json'
  if (Test-Path $envFile) {
    $envJson = Get-Content $envFile -Raw | ConvertFrom-Json
    foreach ($p in $envJson.PSObject.Properties) {
      [Environment]::SetEnvironmentVariable($p.Name, $p.Value, 'Machine')
    }
  }

  Step 'start-sshd'
  Set-Service -Name sshd -StartupType Automatic
  Start-Service -Name sshd
  (Get-Service sshd).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

  Step 'ready'
  $ok = @{ ok = $true; user = '{{SANDBOX_USER}}'; utc = (Get-Date).ToUniversalTime().ToString('o') }
  Set-Content -Path 'C:\airlock\ready.json' -Value ($ok | ConvertTo-Json -Compress)
}
catch {
  $bad = @{ ok = $false; phase = $phase; error = $_.Exception.Message
            script = "$($_.InvocationInfo.PositionMessage)" }
  Set-Content -Path 'C:\airlock\ready.json' -Value ($bad | ConvertTo-Json -Compress)
  throw
}
finally { try { Stop-Transcript | Out-Null } catch {} }
