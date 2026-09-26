#Requires -RunAsAdministrator
param(
  [string]$ServerUrl = "",
  [string]$DeviceId = "",
  [string]$Token = "",
  [string]$ChildAccount = "",
  [string]$InstallDir = "C:\Program Files\TooMuch"
)
$ErrorActionPreference = "Stop"
$dataDir = "C:\ProgramData\TooMuch"
$identityFile = Join-Path $dataDir "device.json"
$saved = if (Test-Path $identityFile) { Get-Content $identityFile -Raw | ConvertFrom-Json }
if (!$ServerUrl) { $ServerUrl = if ($saved.serverUrl) { $saved.serverUrl } else { "http://raspberrypi.local:3020" } }
if ((!$DeviceId) -xor (!$Token)) { throw "Supply both DeviceId and Token, or neither." }
if (!$DeviceId -and $saved.deviceId -and $saved.token) {
  if ($saved.serverUrl.TrimEnd('/') -ne $ServerUrl.TrimEnd('/')) { throw "Server changed: supply an explicit DeviceId and Token." }
  $DeviceId = $saved.deviceId
  $Token = $saved.token
}

# Elevated credentials may belong to a parent. Use Explorer's owner in THIS
# interactive session, never the elevated process's username.
if ($ChildAccount) {
  $childSid = ([Security.Principal.NTAccount]$ChildAccount).Translate([Security.Principal.SecurityIdentifier]).Value
} elseif ($saved.childSid) {
  $childSid = $saved.childSid
} else {
  $sessionId = (Get-Process -Id $PID).SessionId
  $owners = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" |
    Where-Object { $_.SessionId -eq $sessionId } |
    ForEach-Object { Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid } |
    Where-Object { $_.ReturnValue -eq 0 } | Select-Object -ExpandProperty Sid -Unique)
  if ($owners.Count -ne 1) { throw "Cannot identify the child session. Supply -ChildAccount 'COMPUTER\child'." }
  $childSid = $owners[0]
}
$account = ([Security.Principal.SecurityIdentifier]$childSid).Translate([Security.Principal.NTAccount]).Value
Write-Host "Protected account: $account ($childSid)"
$source = Join-Path $PSScriptRoot "TooMuch.exe"
if (!(Test-Path $source)) { throw "TooMuch.exe is missing from this bundle." }

# Stop every legacy launcher before replacing the binary.
foreach ($task in @('TooMuch', 'TooMuchTray')) {
  Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
  Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
}
$service = Get-Service TooMuch -ErrorAction SilentlyContinue
if ($service) { Stop-Service TooMuch -Force }
Get-Process TooMuch -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $InstallDir, $dataDir | Out-Null
Copy-Item $source (Join-Path $InstallDir 'TooMuch.exe') -Force

if (!$DeviceId) {
  $reg = Invoke-RestMethod -Method Post -Uri "$($ServerUrl.TrimEnd('/'))/api/register" -ContentType 'application/json' -Body (@{hostname=$env:COMPUTERNAME} | ConvertTo-Json)
  $DeviceId = $reg.device_id
  $Token = $reg.token
}
New-Item 'HKLM:\SOFTWARE\TooMuch' -Force | Out-Null
foreach ($entry in @{ServerUrl=$ServerUrl; DeviceId=$DeviceId; Token=$Token; ChildSid=$childSid; InstallDir=$InstallDir}.GetEnumerator()) {
  New-ItemProperty 'HKLM:\SOFTWARE\TooMuch' -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null
}
@{serverUrl=$ServerUrl; deviceId=$DeviceId; token=$Token; childSid=$childSid} | ConvertTo-Json | Set-Content $identityFile -Encoding UTF8
$registryAcl = New-Object Security.AccessControl.RegistrySecurity
$registryAcl.SetAccessRuleProtection($true, $false)
$registryAcl.SetOwner([Security.Principal.SecurityIdentifier]'S-1-5-32-544')
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
  $rule = New-Object Security.AccessControl.RegistryAccessRule([Security.Principal.SecurityIdentifier]$sid, 'FullControl', 'ContainerInherit', 'None', 'Allow')
  $registryAcl.AddAccessRule($rule)
}
Set-Acl 'HKLM:\SOFTWARE\TooMuch' $registryAcl

# The service owns all mutable data and credentials; user processes do not write it.
foreach ($folder in @($InstallDir, $dataDir)) {
  $acl = New-Object Security.AccessControl.DirectorySecurity
  $acl.SetAccessRuleProtection($true, $false)
  $acl.SetOwner([Security.Principal.SecurityIdentifier]'S-1-5-32-544')
  foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $rule = New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.SecurityIdentifier]$sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
  }
  Set-Acl $folder $acl
  # Remove old explicit per-user grants from files left by tray versions.
  Get-ChildItem $folder -Recurse -Force | ForEach-Object {
    $childAcl = Get-Acl $_.FullName
    $childAcl.SetAccessRuleProtection($false, $false)
    foreach ($rule in @($childAcl.Access | Where-Object { !$_.IsInherited })) { [void]$childAcl.RemoveAccessRuleSpecific($rule) }
    $childAcl.SetOwner([Security.Principal.SecurityIdentifier]'S-1-5-32-544')
    Set-Acl $_.FullName $childAcl
  }
}
$binPath = '"' + (Join-Path $InstallDir 'TooMuch.exe') + '" --service'
if ($service) {
  $cim = Get-CimInstance Win32_Service -Filter "Name='TooMuch'"
  $result = Invoke-CimMethod $cim -MethodName Change -Arguments @{PathName=$binPath; StartMode='Automatic'; StartName='LocalSystem'}
  if ($result.ReturnValue -ne 0) { throw "Service update failed: $($result.ReturnValue)" }
} else {
  New-Service TooMuch -BinaryPathName $binPath -DisplayName 'TooMuch' -StartupType Automatic | Out-Null
}
sc.exe failure TooMuch reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service recovery configuration failed." }
Start-Service TooMuch
(Get-Service TooMuch).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
Write-Host "OK: $DeviceId installed for $account. Edit limits at $ServerUrl/admin"
