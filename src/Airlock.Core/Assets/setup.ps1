#requires -Version 5.1
# Airlock in-guest provisioning. Runs as SYSTEM via `wsb exec -r System`, before the desktop logs on.
#
# Everything here has to happen before the window opens, because the interactive session inherits
# machine environment at logon and never re-reads it afterwards.
#
# Tokens: {{PATH_PREPEND}} {{BLOCK_LAN}} {{ALLOW_LIST}}
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$out = 'C:\airlock\_out_'
New-Item -ItemType Directory -Force -Path 'C:\airlock\_setup_' | Out-Null
Start-Transcript -Path 'C:\airlock\_setup_\setup.log' -Force | Out-Null
$phase = 'start'

# The host cannot see this script's output - wsb exec returns nothing - so each step is also
# written into the shared folder, which is what lets `airlock start` show where it has got to and
# name the step it was on if it never finishes.
function Step($n) {
  $script:phase = $n
  Write-Host "== AIRLOCK STEP: $n"
  try { Set-Content -Path (Join-Path $out 'phase.txt') -Value $n -ErrorAction SilentlyContinue } catch { }
}

try {
  Step 'machine-env'
  $envKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'

  # Read Path WITHOUT expanding, or we bake in literals and demote REG_EXPAND_SZ to REG_SZ.
  $rawPath = (Get-Item -LiteralPath $envKey).GetValue('Path', '', 'DoNotExpandEnvironmentNames')
  Set-ItemProperty -LiteralPath $envKey -Name 'Path' -Value ('{{PATH_PREPEND}}' + ';' + $rawPath) -Type ExpandString

  $envFile = 'C:\airlock\_session_\env.json'
  if (Test-Path $envFile) {
    $envJson = Get-Content $envFile -Raw | ConvertFrom-Json
    foreach ($p in $envJson.PSObject.Properties) {
      [Environment]::SetEnvironmentVariable($p.Name, $p.Value, 'Machine')
    }
  }

  # --- credentials -----------------------------------------------------------------------------
  # Handed over through the writable folder rather than a command line, so the value never appears
  # in a process listing on either side. Deleted the moment it has been read; the host deletes it
  # again afterwards, in case this script dies in between.
  Step 'credentials'
  $secretFile = Join-Path $out 'secrets.json'
  if (Test-Path $secretFile) {
    try {
      $secrets = Get-Content $secretFile -Raw | ConvertFrom-Json
      foreach ($p in $secrets.PSObject.Properties) {
        [Environment]::SetEnvironmentVariable($p.Name, $p.Value, 'Machine')
      }
    }
    finally {
      Remove-Item $secretFile -Force -ErrorAction SilentlyContinue
    }
  }

  # --- network ---------------------------------------------------------------------------------
  # The guest's own subnet and gateway sit inside 172.16.0.0/12, so blocking private ranges wholesale
  # severs its own DNS and default route. Allow its prefix and gateway first; Windows Firewall
  # applies the more specific allow over the broad block.
  if ('{{BLOCK_LAN}}' -eq 'true') {
    Step 'network'
    $ownPrefixes = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
      Where-Object { $_.IPAddress -notlike '127.*' } |
      ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" })

    $gateways = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
      ForEach-Object { $_.NextHop } | Where-Object { $_ -and $_ -ne '0.0.0.0' })

    $allow = @($ownPrefixes + $gateways + @({{ALLOW_LIST}})) | Where-Object { $_ } | Select-Object -Unique
    if ($allow.Count -gt 0) {
      New-NetFirewallRule -Name 'airlock-allow-own' -DisplayName 'Airlock: own subnet and gateway' `
        -Direction Outbound -Action Allow -RemoteAddress $allow -Enabled True | Out-Null
    }

    New-NetFirewallRule -Name 'airlock-block-lan' -DisplayName 'Airlock: block private ranges' `
      -Direction Outbound -Action Block -Enabled True `
      -RemoteAddress @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '169.254.0.0/16') | Out-Null

    Write-Host "allowed through: $($allow -join ', ')"
  }

  Step 'ready'
  $ok = @{ ok = $true; utc = (Get-Date).ToUniversalTime().ToString('o') }
  Set-Content -Path (Join-Path $out 'ready.json') -Value ($ok | ConvertTo-Json -Compress)
}
catch {
  # The host has no other way to learn what happened: wsb exec returns neither output nor the
  # remote exit code, so this file is the whole channel.
  $bad = @{ ok = $false; phase = $phase; error = $_.Exception.Message
            script = "$($_.InvocationInfo.PositionMessage)" }
  New-Item -ItemType Directory -Force -Path $out | Out-Null
  Set-Content -Path (Join-Path $out 'ready.json') -Value ($bad | ConvertTo-Json -Compress)
  Copy-Item 'C:\airlock\_setup_\setup.log' $out -Force -ErrorAction SilentlyContinue
  throw
}
finally { try { Stop-Transcript | Out-Null } catch {} }
