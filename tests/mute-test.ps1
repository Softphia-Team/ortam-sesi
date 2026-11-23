# "Keep playing when the PC is muted" test (on the real device):
#  1) While the app runs, the device is muted (simulates the mute key)
#     -> expected: the device is unmuted again, the other apps are muted one by one, the ambient sound keeps playing
#  2) A new app starts playing during the takeover -> it is muted too
#  3) The mute key is pressed again -> everything goes back to normal (apps are unmuted)
#  4) The app exits -> no app is left muted
# Note: other sounds playing during the test (e.g. a browser) are silent for 5-10 s.
param([string]$Exe = "", [string]$OutDir = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'src\OrtamSesi\bin\Release\net8.0-windows\OrtamSesi.exe' }
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ('OrtamSesi-mute-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$set = Join-Path $OutDir 'settings.json'
$log = Join-Path $OutDir 'log.txt'
$assets = Join-Path $PSScriptRoot 'assets'
if (-not (Test-Path (Join-Path $assets 'noise8s.wav'))) { python (Join-Path $PSScriptRoot 'make_test_sounds.py') $assets }
[System.IO.File]::WriteAllText($set, '{ "Playing": true, "Sound": "waterfall", "Volume": 5, "KeepPlayingWhenMuted": true, "WelcomeShown": true, "Advanced": { "DebugLog": false } }')

$fails = 0
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "  [OK ] $what" } else { Write-Host "  [FAIL] $what"; $script:fails++ } }
function State() {
    $f = Join-Path $OutDir 'state.txt'
    Start-Process -FilePath $Exe -ArgumentList @('--mute-state', '--out', "`"$f`"") -Wait | Out-Null
    $lines = Get-Content $f -Encoding UTF8
    $dev = $lines | Where-Object { $_ -like 'device:*' } | Select-Object -First 1
    $sessions = $lines | Where-Object { $_ -like 'session:*' } | ForEach-Object {
        if ($_ -match 'session: (\S+) pid=(\d+) state=(\d) muted=(\d)') { [pscustomobject]@{ App = $matches[1]; Pid = [int]$matches[2]; State = [int]$matches[3]; Muted = $matches[4] -eq '1' } }
    }
    [pscustomobject]@{ DeviceMuted = $dev -match 'muted=1'; Sessions = @($sessions); Raw = ($lines -join "`n") }
}
function SetMute([string]$onOff) { Start-Process -FilePath $Exe -ArgumentList @('--set-mute', $onOff) -Wait | Out-Null }

$p = Start-Process -FilePath $Exe -ArgumentList @('--settings', "`"$set`"") -PassThru
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not ((Test-Path $log) -and (Select-String -Path $log -Pattern 'Started' -Quiet))) { if ($sw.Elapsed.TotalSeconds -gt 60) { throw 'the app did not start' }; Start-Sleep -Milliseconds 200 }
Start-Sleep -Seconds 2
$before = State
Write-Host "Start: device muted=$($before.DeviceMuted); sessions already muted: $((@($before.Sessions | Where-Object Muted | ForEach-Object App)) -join ', ')"
$initiallyMuted = @($before.Sessions | Where-Object Muted | ForEach-Object Pid)

try {
    Write-Host '1) Muting the device (mute key)'
    SetMute 'on'
    Start-Sleep -Milliseconds 1500
    $s1 = State
    Check (-not $s1.DeviceMuted) 'the app unmuted the device again (the speaker stays on)'
    $others = @($s1.Sessions | Where-Object { $_.App -ne 'ortamsesi' -and $_.Pid -ne 0 })
    $notMuted = @($others | Where-Object { -not $_.Muted })
    Check ($others.Count -eq 0 -or $notMuted.Count -eq 0) "the other apps were muted ($($others.Count) sessions; not muted: $(($notMuted | ForEach-Object App) -join ', '))"
    $self = @($s1.Sessions | Where-Object { $_.App -eq 'ortamsesi' -and $_.Pid -eq $p.Id })
    Check ($self.Count -ge 1 -and -not $self[0].Muted) "the ambient sound's own session is not muted (keeps playing)"
    Check ([bool](Select-String -Path $log -Pattern 'PC muted' -Quiet)) 'the log has the takeover entry'

    Write-Host '2) A new app starts playing during the takeover'
    $player = New-Object System.Media.SoundPlayer (Join-Path $assets 'noise8s.wav')
    $player.Play()
    Start-Sleep -Milliseconds 2500
    $s2 = State
    $ps = @($s2.Sessions | Where-Object { $_.Pid -eq $PID })
    Check ($ps.Count -ge 1 -and $ps[0].Muted) 'the newly started app (this PowerShell) was muted too'
    $player.Stop()

    Write-Host '3) Pressing the mute key again (unmute)'
    SetMute 'on'
    Start-Sleep -Milliseconds 1500
    $s3 = State
    Check (-not $s3.DeviceMuted) 'the device is not muted'
    $stillMuted = @($s3.Sessions | Where-Object { $_.Muted -and ($initiallyMuted -notcontains $_.Pid) -and $_.App -ne 'ortamsesi' })
    Check ($stillMuted.Count -eq 0) "the apps were unmuted (still muted: $(($stillMuted | ForEach-Object App) -join ', '))"
    Check ([bool](Select-String -Path $log -Pattern 'user unmuted' -Quiet)) 'the log has the "user unmuted" entry'
}
finally {
    Start-Process -FilePath $Exe -ArgumentList @('--exit', '--settings', "`"$set`"") -Wait | Out-Null
    if (-not $p.WaitForExit(6000)) { Stop-Process -Id $p.Id -Force }
    $end = State
    if ($end.DeviceMuted -and -not $before.DeviceMuted) { SetMute 'off'; Write-Host 'WARNING: the device was left muted; unmuted it' }
}
$stillMuted = @($end.Sessions | Where-Object { $_.Muted -and ($initiallyMuted -notcontains $_.Pid) })
Check ($stillMuted.Count -eq 0) '4) no app was left muted after the app exited'
Check (-not (Test-Path (Join-Path $OutDir 'muted-sessions.json'))) 'the mute list was cleaned up'
Write-Host ''
Write-Host '--- log ---'
Get-Content $log -Encoding UTF8 | Where-Object { $_ -notmatch '\[debug\]' }
Write-Host ''
Write-Host $(if ($fails -eq 0) { 'RESULT: all checks passed' } else { "RESULT: $fails checks failed" })
exit $fails
