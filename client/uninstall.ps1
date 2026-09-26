#Requires -RunAsAdministrator
$ErrorActionPreference = "Continue"
Stop-ScheduledTask -TaskName "TooMuch" -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName "TooMuch" -Confirm:$false -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName "TooMuchTray" -Confirm:$false -ErrorAction SilentlyContinue
Stop-Service -Name "TooMuch" -Force -ErrorAction SilentlyContinue
& "$env:SystemRoot\System32\sc.exe" delete TooMuch 2>$null | Out-Null
# kill leftover agent processes: a running exe locks InstallDir and
# breaks Copy-Item on reinstall
Get-Process -Name "TooMuch" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Remove-Item -Recurse -Force "C:\Program Files\TooMuch" -ErrorAction SilentlyContinue
Remove-Item -Path "HKLM:\SOFTWARE\TooMuch" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Uninstalled (device.json in ProgramData kept for history)."
