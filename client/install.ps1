#Requires -RunAsAdministrator
param(
  [string]$ServerUrl = "http://raspberrypi.local:3020",
  [string]$DeviceId = "",
  [string]$Token = "",
  [string]$InstallDir = "C:\Program Files\ScreenTime"
)
$ErrorActionPreference = "Stop"

$exe = Join-Path $InstallDir "ScreenTime.exe"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot "ScreenTime.exe") -Destination $exe -Force

# enrollment: auto-register when DeviceId/Token not given
if ([string]::IsNullOrWhiteSpace($DeviceId) -or [string]::IsNullOrWhiteSpace($Token)) {
  Write-Host "Auto-registering $($env:COMPUTERNAME) at $ServerUrl ..."
  $body = @{ hostname = $env:COMPUTERNAME } | ConvertTo-Json
  $reg = Invoke-RestMethod -Method Post -Uri "$ServerUrl/api/register" -Body $body -ContentType "application/json"
  $DeviceId = $reg.device_id
  $Token = $reg.token
  Write-Host "Registered as $DeviceId"
}

# persist for SYSTEM service (HKLM, admin-only) + ProgramData fallback
New-Item -Path "HKLM:\SOFTWARE\ScreenTime" -Force | Out-Null
New-ItemProperty -Path "HKLM:\SOFTWARE\ScreenTime" -Name "ServerUrl" -Value $ServerUrl -Force | Out-Null
New-ItemProperty -Path "HKLM:\SOFTWARE\ScreenTime" -Name "DeviceId" -Value $DeviceId -Force | Out-Null
New-ItemProperty -Path "HKLM:\SOFTWARE\ScreenTime" -Name "Token" -Value $Token -Force | Out-Null
$dataDir = "C:\ProgramData\ScreenTime"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
@{ serverUrl = $ServerUrl; deviceId = $DeviceId; token = $Token } | ConvertTo-Json | Out-File (Join-Path $dataDir "device.json") -Encoding utf8

# lock down files: Users = read-only (child is Standard User)
icacls $InstallDir /inheritance:r | Out-Null
icacls $InstallDir /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "Users:(OI)(CI)R" | Out-Null

# service (SYSTEM, auto, restart on failure)
$svc = "ScreenTime"
sc.exe delete $svc 2>$null | Out-Null
sc.exe create $svc binPath= "`"$exe`" --service" start= auto obj= LocalSystem | Out-Null
sc.exe failure $svc reset= 5 actions= restart/5000/restart/5000/restart/5000 | Out-Null
sc.exe start $svc | Out-Null

# tray autostart for every user
$task = "ScreenTimeTray"
schtasks /delete /tn $task /f 2>$null | Out-Null
schtasks /create /tn $task /tr "`"$exe`" --tray" /sc onlogon /rl highest /f | Out-Null

Write-Host "OK: $DeviceId installed. Edit limits at $ServerUrl/admin"
