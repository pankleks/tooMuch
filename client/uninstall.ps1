#Requires -RunAsAdministrator
$ErrorActionPreference = "Continue"
sc.exe stop ScreenTime 2>$null | Out-Null
sc.exe delete ScreenTime 2>$null | Out-Null
schtasks /delete /tn ScreenTimeTray /f 2>$null | Out-Null
Remove-Item -Recurse -Force "C:\Program Files\ScreenTime" -ErrorAction SilentlyContinue
Remove-Item -Path "HKLM:\SOFTWARE\ScreenTime" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Uninstalled (device.json in ProgramData kept for history)."
