#Requires -RunAsAdministrator
$ErrorActionPreference = "Continue"
sc.exe stop TooMuch 2>$null | Out-Null
sc.exe delete TooMuch 2>$null | Out-Null
schtasks /delete /tn TooMuchTray /f 2>$null | Out-Null
Remove-Item -Recurse -Force "C:\Program Files\TooMuch" -ErrorAction SilentlyContinue
Remove-Item -Path "HKLM:\SOFTWARE\TooMuch" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Uninstalled (device.json in ProgramData kept for history)."
