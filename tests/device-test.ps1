# Device test (while running):
#  1) Plays on the default device
#  2) Another device is chosen in the settings file -> the old stream closes, the new one opens (file watcher + instant switch)
#  3) A device that does not exist is chosen -> "Waiting for speaker", retries without crashing
#  4) Back to the Windows default -> opens again, fades in from silence
#  5) Pause + keep-awake off -> the stream closes completely after 10 s (speaker/PC can sleep)
#  6) Resume -> the stream opens again
param([string]$Exe = "", [string]$OutDir = "", [string]$OtherDeviceId = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'src\OrtamSesi\bin\Release\net8.0-windows\OrtamSesi.exe' }
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ('OrtamSesi-device-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$set = Join-Path $OutDir 'settings.json'
$log = Join-Path $OutDir 'log.txt'

function Write-Settings([string]$deviceJson, [bool]$playing, [bool]$keep) {
    $p = if ($playing) { 'true' } else { 'false' }
    $k = if ($keep) { 'true' } else { 'false' }
    $json = '{ "Playing": ' + $p + ', "Sound": "waterfall", "Volume": 5, "KeepPlayingWhenMuted": false, "WelcomeShown": true, "DeviceId": ' + $deviceJson + ', "DeviceName": "test", "Advanced": { "KeepSpeakerAwake": ' + $k + ', "IgnoreApps": ["brave"] } }'
    [System.IO.File]::WriteAllText($set, $json)
}
function Mark([string]$name) { Write-Host ((Get-Date).ToString('HH:mm:ss.fff') + "  >>> $name") }

Write-Settings 'null' $true $true
$p = Start-Process -FilePath $Exe -ArgumentList @('--settings', "`"$set`"") -PassThru
Start-Sleep -Seconds 8

if ($OtherDeviceId) {
    Mark "other device: $OtherDeviceId"
    Write-Settings ('"' + $OtherDeviceId + '"') $true $true
    Start-Sleep -Seconds 8
}

Mark 'device that does not exist'
Write-Settings '"{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}"' $true $true
Start-Sleep -Seconds 8

Mark 'Windows default'
Write-Settings 'null' $true $true
Start-Sleep -Seconds 8

Mark 'pause + keep-awake off'
Write-Settings 'null' $false $false
Start-Sleep -Seconds 15

Mark 'resume'
Write-Settings 'null' $true $true
Start-Sleep -Seconds 5

Start-Process -FilePath $Exe -ArgumentList @('--exit', '--settings', "`"$set`"") -Wait | Out-Null
if (-not $p.WaitForExit(6000)) { Write-Host 'WARNING: did not exit'; Stop-Process -Id $p.Id -Force }
Write-Host ''
Write-Host '--- log ---'
Get-Content $log -Encoding UTF8
