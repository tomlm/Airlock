#requires -Version 5.1
# Airlock in-guest provisioning. Runs as SYSTEM via `wsb exec -r System`, before the desktop logs on.
#
# Everything here has to happen before the window opens, because the interactive session inherits
# machine environment at logon and never re-reads it afterwards.
#
# Tokens: {{PATH_PREPEND}} {{BLOCK_LAN}} {{ALLOW_LIST}}
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$out = 'C:\out'
New-Item -ItemType Directory -Force -Path 'C:\setup' | Out-Null
Start-Transcript -Path 'C:\setup\setup.log' -Force | Out-Null
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

  $envFile = 'C:\session\env.json'
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
  # Windows Firewall evaluates block rules BEFORE allow rules, and an explicit block beats an
  # explicit allow however specific that allow is - the only exception being an authenticated-bypass
  # rule, which needs IPsec. So the guest's own subnet cannot be carved back out with a second rule.
  # An earlier version tried exactly that, and the allow rule sat there enabled and inert while name
  # resolution timed out: the sandbox's DNS server IS its default gateway, and lives inside
  # 172.16.0.0/12. Raw IP traffic kept working, which made it look like a DNS fault rather than a
  # firewall one.
  #
  # So the exemptions are subtracted from the blocked ranges before the rule is written. What goes
  # into the rule is already the right set, and there is no second rule to be overruled.
  if ('{{BLOCK_LAN}}' -eq 'true') {
    Step 'network'

    function ConvertTo-UInt32Ip([string]$ip) {
      $b = ([System.Net.IPAddress]::Parse($ip)).GetAddressBytes()
      [Array]::Reverse($b)
      return [BitConverter]::ToUInt32($b, 0)
    }

    function ConvertFrom-UInt32Ip([uint32]$v) {
      $b = [BitConverter]::GetBytes($v)
      [Array]::Reverse($b)
      return ([System.Net.IPAddress]::new($b)).IPAddressToString
    }

    function Get-MaskFor([int]$prefix) {
      if ($prefix -le 0)  { return [uint32]0 }
      if ($prefix -ge 32) { return [uint32]4294967295 }
      return [uint32](([uint64]4294967295 -shl (32 - $prefix)) -band [uint64]4294967295)
    }

    function Get-NetworkOf([string]$cidr) {
      $parts = $cidr -split '/'
      $ip = ConvertTo-UInt32Ip $parts[0]
      return [uint32]($ip -band (Get-MaskFor ([int]$parts[1])))
    }

    # Is $inner wholly contained in $outer?
    function Test-Inside([string]$inner, [string]$outer) {
      $ip = [int]($inner -split '/')[1]
      $op = [int]($outer -split '/')[1]
      if ($ip -lt $op) { return $false }
      $mask = Get-MaskFor $op
      return ([uint32]((ConvertTo-UInt32Ip (($inner -split '/')[0])) -band $mask)) -eq `
             ([uint32]((ConvertTo-UInt32Ip (($outer -split '/')[0])) -band $mask))
    }

    # $block minus $exempt, as the CIDRs covering what is left: walk from the block's prefix down to
    # the exemption's, taking the sibling at each level. Yields nothing when the two are the same
    # network, which is what should happen.
    function Remove-Subnet([string]$block, [string]$exempt) {
      $bp = [int]($block -split '/')[1]
      $ep = [int]($exempt -split '/')[1]
      $eip = ConvertTo-UInt32Ip (($exempt -split '/')[0])
      $out = @()
      for ($p = $bp + 1; $p -le $ep; $p++) {
        $net = [uint32]($eip -band (Get-MaskFor $p))
        $bit = [uint32]([uint64]1 -shl (32 - $p))
        $out += "$(ConvertFrom-UInt32Ip ([uint32]([uint64]$net -bxor [uint64]$bit)))/$p"
      }
      return $out
    }

    # What the guest has to keep reaching to stay a working machine: its own subnet, its default
    # gateway, and its DNS servers - the last of which is the whole reason this care is needed.
    $exempt = @()

    foreach ($a in @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                     Where-Object { $_.IPAddress -notlike '127.*' })) {
      $exempt += "$(ConvertFrom-UInt32Ip (Get-NetworkOf "$($a.IPAddress)/$($a.PrefixLength)"))/$($a.PrefixLength)"
    }

    foreach ($g in @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                     ForEach-Object { $_.NextHop } | Where-Object { $_ -and $_ -ne '0.0.0.0' })) {
      $exempt += "$g/32"
    }

    foreach ($d in @(Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                     ForEach-Object { $_.ServerAddresses } | Where-Object { $_ })) {
      $exempt += "$d/32"
    }

    foreach ($a in @({{ALLOW_LIST}})) {
      if ($a) { if ($a -match '/') { $exempt += $a } else { $exempt += "$a/32" } }
    }

    $exempt = @($exempt | Select-Object -Unique)

    $blocked = @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '169.254.0.0/16')

    foreach ($ex in $exempt) {
      $next = @()
      foreach ($b in $blocked) {
        if (Test-Inside $ex $b)    { $next += (Remove-Subnet $b $ex) }
        elseif (Test-Inside $b $ex) { }   # the exemption swallows this range whole
        else                        { $next += $b }
      }
      $blocked = @($next)
    }

    # An allow rule here would be worse than useless: it cannot override the block above, and it
    # would read like a protection that is not there.
    Remove-NetFirewallRule -Name 'airlock-allow-own' -ErrorAction SilentlyContinue

    if ($blocked.Count -gt 0) {
      New-NetFirewallRule -Name 'airlock-block-lan' -DisplayName 'Airlock: block private ranges' `
        -Direction Outbound -Action Block -Enabled True -RemoteAddress $blocked | Out-Null
    }

    Write-Host "exempt:  $($exempt -join ', ')"
    Write-Host "blocked: $($blocked -join ', ')"
  }

  # A working machine resolves names. This is checked rather than assumed because the firewall rules
  # above can take DNS down while leaving raw IP traffic working, which is a hard failure to read
  # from inside a sandbox with no shell history.
  Step 'dns'
  $dns = $false
  try {
    $null = Resolve-DnsName 'www.microsoft.com' -Type A -ErrorAction Stop
    $dns = $true
  }
  catch {
    Write-Host "dns check failed: $($_.Exception.Message)"
  }

  # --- url handler -----------------------------------------------------------------------------
  # Windows Sandbox ships Edge on disk but nothing registers it with the shell, so ShellExecute has
  # no command to run for an https:// link. The failure is completely silent: `Start-Process
  # https://...` returns without an exception, `rundll32 url.dll,FileProtocolHandler` exits 0, and
  # no browser ever appears. Anything that hands a link to the desktop - an OAuth sign-in, a tool
  # printing a clickable URL - just does nothing. Launching Edge by full path works the whole time,
  # which makes it look like a network fault rather than a missing file association.
  #
  # Registered under HKLM\SOFTWARE\Classes rather than per-user: HKCR is the merge of that with
  # HKCU\SOFTWARE\Classes, this runs as SYSTEM before anyone has logged on, and it covers whichever
  # account the desktop ends up using. Edge's own `--make-default-browser` is not an option - on
  # current Windows it answers by opening the Settings app rather than registering anything.
  Step 'browser'
  $browser = $false
  try {
    $edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
              "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1

    if ($edge) {
      # --single-argument takes the rest of the command line verbatim, so a URL containing spaces
      # or quotes cannot be split into extra arguments. This is what Edge registers for itself.
      $command = '"' + $edge + '" --single-argument %1'

      foreach ($scheme in 'http', 'https') {
        $base = "HKLM:\SOFTWARE\Classes\$scheme"
        New-Item -Path "$base\shell\open\command" -Force | Out-Null
        Set-ItemProperty -LiteralPath $base -Name '(default)' -Value "URL:$scheme"
        Set-ItemProperty -LiteralPath $base -Name 'URL Protocol' -Value ''
        Set-ItemProperty -LiteralPath "$base\shell\open\command" -Name '(default)' -Value $command
      }

      $browser = $true
      Write-Host "url handler -> $edge"
    }
    else {
      Write-Host 'no Edge on this image; http(s) links will not open'
    }
  }
  catch {
    Write-Host "url handler failed: $($_.Exception.Message)"
  }

  # --- drive letter ----------------------------------------------------------------------------
  # A: is every airlock, one letter and a name away - which keeps working paths well clear of
  # MAX_PATH once a build starts nesting. The folder has to exist first: with no airlocks configured
  # nothing is mounted under it, so `airlock start` on a fresh machine would otherwise have nothing
  # to subst onto.
  #
  # Whether this is visible to the desktop is the open question. DOS device maps are per-logon
  # session, and this runs as SYSTEM while the desktop runs as WDAGUtilityAccount - but SYSTEM's map
  # is the global one, so the link should land in \GLOBAL?? and be seen everywhere. The verdict goes
  # into ready.json either way, so the host knows which spelling of a path it can hand out.
  Step 'drive'
  $drive = $false
  try {
    New-Item -ItemType Directory -Force -Path 'C:\airlocks' | Out-Null
    & subst.exe 'A:' 'C:\airlocks' 2>&1 | Out-Null
    $drive = Test-Path 'A:\'
    Write-Host "A: -> C:\airlocks : $drive"
  }
  catch {
    Write-Host "subst failed: $($_.Exception.Message)"
  }

  Step 'ready'
  $ok = @{ ok = $true; drive = $drive; dns = $dns; browser = $browser
           utc = (Get-Date).ToUniversalTime().ToString('o') }
  Set-Content -Path (Join-Path $out 'ready.json') -Value ($ok | ConvertTo-Json -Compress)
}
catch {
  # The host has no other way to learn what happened: wsb exec returns neither output nor the
  # remote exit code, so this file is the whole channel.
  $bad = @{ ok = $false; phase = $phase; error = $_.Exception.Message
            script = "$($_.InvocationInfo.PositionMessage)" }
  New-Item -ItemType Directory -Force -Path $out | Out-Null
  Set-Content -Path (Join-Path $out 'ready.json') -Value ($bad | ConvertTo-Json -Compress)
  Copy-Item 'C:\setup\setup.log' $out -Force -ErrorAction SilentlyContinue
  throw
}
finally { try { Stop-Transcript | Out-Null } catch {} }
