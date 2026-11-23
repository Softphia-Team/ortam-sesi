# Ortam Sesi - uninstaller. Stops the program and leaves nothing behind:
# program folder, autostart entry, Start menu shortcut, uninstall entry, settings and log.
$ErrorActionPreference = 'Continue'
Set-Location $env:TEMP

$dest = Join-Path $env:LOCALAPPDATA 'Programs\OrtamSesi'
$exe = Join-Path $dest 'OrtamSesi.exe'
$data = Join-Path $env:LOCALAPPDATA 'OrtamSesi'

Write-Host 'Uninstalling Ortam Sesi...'

# 1) Stop: gentle exit signal first, force if needed
if (Test-Path $exe) { Start-Process -FilePath $exe -ArgumentList '--exit' -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue }
$deadline = (Get-Date).AddSeconds(6)
while ((Get-Process OrtamSesi -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
Get-Process OrtamSesi -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 2) Registry entries and shortcut
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'OrtamSesi' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OrtamSesi' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'Ortam Sesi.lnk') -Force -ErrorAction SilentlyContinue

# 3) Files
Remove-Item -Path $dest -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $data -Recurse -Force -ErrorAction SilentlyContinue

if ((Test-Path $dest) -or (Test-Path $data)) {
    Write-Host 'Some files could not be deleted (they may be in use). Restart the PC and try again:' -ForegroundColor Yellow
    if (Test-Path $dest) { Write-Host "  $dest" }
    if (Test-Path $data) { Write-Host "  $data" }
    Read-Host 'Press Enter to close'
}
else {
    Write-Host 'Ortam Sesi has been completely removed.' -ForegroundColor Green
    Start-Sleep -Seconds 4
}
