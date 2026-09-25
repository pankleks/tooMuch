#Requires -RunAsAdministrator
param(
  [string]$ServerUrl = "http://raspberrypi.local:3020",
  [string]$DeviceId = "",
  [string]$Token = "",
  [string]$InstallDir = "C:\Program Files\TooMuch"
)
$ErrorActionPreference = "Stop"

$exe = Join-Path $InstallDir "TooMuch.exe"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot "TooMuch.exe") -Destination $exe -Force

# enrollment: auto-register when DeviceId/Token not given
if ([string]::IsNullOrWhiteSpace($DeviceId) -or [string]::IsNullOrWhiteSpace($Token)) {
  Write-Host "Auto-registering $($env:COMPUTERNAME) at $ServerUrl ..."
  $body = @{ hostname = $env:COMPUTERNAME } | ConvertTo-Json
  $reg = Invoke-RestMethod -Method Post -Uri "$ServerUrl/api/register" -Body $body -ContentType "application/json"
  $DeviceId = $reg.device_id
  $Token = $reg.token
  Write-Host "Registered as $DeviceId"
}

# persist for SYSTEM task (HKLM, admin-only) + ProgramData fallback
New-Item -Path "HKLM:\SOFTWARE\TooMuch" -Force | Out-Null
New-ItemProperty -Path "HKLM:\SOFTWARE\TooMuch" -Name "ServerUrl" -Value $ServerUrl -Force | Out-Null
New-ItemProperty -Path "HKLM:\SOFTWARE\TooMuch" -Name "DeviceId" -Value $DeviceId -Force | Out-Null
New-ItemProperty -Path "HKLM:\SOFTWARE\TooMuch" -Name "Token" -Value $Token -Force | Out-Null
$dataDir = "C:\ProgramData\TooMuch"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
@{ serverUrl = $ServerUrl; deviceId = $DeviceId; token = $Token } | ConvertTo-Json | Out-File (Join-Path $dataDir "device.json") -Encoding utf8

# lock down files: Users = read-only (child is Standard User)
icacls $InstallDir /inheritance:r | Out-Null
icacls $InstallDir /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "Users:(OI)(CI)R" | Out-Null

# enforcement loop (SYSTEM, at startup, restart on failure)
# NOTE: --service runs as a SYSTEM scheduled task, NOT an SCM service.
# The exe is a plain console loop (Program.cs RunServiceAsync) and never
# signals the Service Control Manager, so New-Service/Start-Service always
# ends in a 7000/7009 start timeout. A startup task needs no SCM handshake.
# Session-0 bonus: GetLastInputInfo/LockWorkStation are per-session APIs;
# in-session enforcement is done by the tray task below.
$svc = "TooMuch"
$legacySvc = Get-Service -Name $svc -ErrorAction SilentlyContinue
if ($legacySvc) {
  Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
  & "$env:SystemRoot\System32\sc.exe" delete $svc | Out-Null
}
Stop-ScheduledTask -TaskName $svc -ErrorAction SilentlyContinue
$svcAction = New-ScheduledTaskAction -Execute $exe -Argument "--service"
$svcTrigger = New-ScheduledTaskTrigger -AtStartup
$svcPrincipal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$svcSettings = New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
Register-ScheduledTask -TaskName $svc -Action $svcAction -Trigger $svcTrigger -Principal $svcPrincipal -Settings $svcSettings -Force | Out-Null
Start-ScheduledTask -TaskName $svc

# tray autostart for every user
# NOTE: ScheduledTask cmdlets are used instead of schtasks.exe: schtasks
# needs '/tr "<exe>" --tray' as raw text and PowerShell strips the inner
# quotes (=> 'Invalid argument/option - Files\TooMuch\...'), and
# 'schtasks /delete' on a missing task aborts the script because
# $ErrorActionPreference = "Stop". Register-ScheduledTask -Force overwrites.
$task = "TooMuchTray"
$taskAction = New-ScheduledTaskAction -Execute $exe -Argument "--tray"
$taskTrigger = New-ScheduledTaskTrigger -AtLogOn
$taskPrincipal = New-ScheduledTaskPrincipal -GroupId "BUILTIN\Users" -RunLevel Highest
Register-ScheduledTask -TaskName $task -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal -Force | Out-Null

# verify both tasks exist (throws on missing -> loud failure, not silent)
Get-ScheduledTask -TaskName $svc -ErrorAction Stop | Out-Null
Get-ScheduledTask -TaskName $task -ErrorAction Stop | Out-Null

Write-Host "OK: $DeviceId installed. Edit limits at $ServerUrl/admin"
