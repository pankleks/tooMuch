#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
$installDir = (Get-ItemProperty 'HKLM:\SOFTWARE\TooMuch' -ErrorAction SilentlyContinue).InstallDir
if (!$installDir) { $installDir = 'C:\Program Files\TooMuch' }
Stop-ScheduledTask -TaskName "TooMuch" -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName "TooMuch" -Confirm:$false -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName "TooMuchTray" -Confirm:$false -ErrorAction SilentlyContinue
if (Get-Service TooMuch -ErrorAction SilentlyContinue) {
  Stop-Service -Name "TooMuch" -Force
  & "$env:SystemRoot\System32\sc.exe" delete TooMuch | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
}
# kill leftover agent processes: a running exe locks InstallDir and
# breaks Copy-Item on reinstall
Get-Process -Name "TooMuch" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir }
if (Test-Path 'HKLM:\SOFTWARE\TooMuch') { Remove-Item 'HKLM:\SOFTWARE\TooMuch' -Recurse -Force }
Write-Host "Uninstalled (device.json in ProgramData kept for history)."
