# Real-device test (on this PC, with the real speaker):
#  Phase 1: is sound currently playing in Brave detected, and does the ambient sound go quiet?
#  Phase 2: short notification -> must not duck; 10 s of audio in Chrome -> must duck and come back;
#           Chrome open but silent -> must not duck; muted Chrome tab -> must not duck;
#           another app (PowerShell) plays 8 s -> must duck and come back;
#           the ambient sound must never detect itself and duck.
# Test sounds are -40 dBFS (very quiet); the ambient sound plays at its normal level.
# Does not touch the user's settings or installed copy (separate settings file, separate Chrome profile).
param(
    [string]$Exe = "",
    [string]$OutDir = "",
    [switch]$SkipBravePhase
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'src\OrtamSesi\bin\Release\net8.0-windows\OrtamSesi.exe' }
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ('OrtamSesi-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$assets = Join-Path $PSScriptRoot 'assets'
if (-not (Test-Path (Join-Path $assets 'noise10s.wav'))) { python (Join-Path $PSScriptRoot 'make_test_sounds.py') $assets }

$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$pageUrl = 'file:///' + ((Join-Path $PSScriptRoot 'play.html') -replace '\\', '/')

$events = New-Object System.Collections.ArrayList
function Mark([string]$name) {
    $t = (Get-Date).ToString('HH:mm:ss.fff')
    [void]$events.Add("$t,$name")
    Write-Host "$t  $name"
}

function New-TestSettings([string]$dir, [string]$ignoreJson) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir 'settings.json'
    $json = '{ "Playing": true, "Sound": "waterfall", "Volume": 1, "KeepPlayingWhenMuted": false, "WelcomeShown": true, "Advanced": { "IgnoreApps": ' + $ignoreJson + ', "DebugLog": true } }'
    [System.IO.File]::WriteAllText($path, $json)
    return $path
}

function Start-App([string]$settings) {
    $script:appProc = Start-Process -FilePath $Exe -ArgumentList @('--settings', "`"$settings`"") -PassThru
    $log = Join-Path (Split-Path $settings) 'log.txt'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not ((Test-Path $log) -and (Select-String -Path $log -Pattern 'Started' -Quiet))) {
        if ($sw.Elapsed.TotalSeconds -gt 60) { throw 'The app did not start within 60 s' }
        Start-Sleep -Milliseconds 100
    }
    Mark ("app-started-" + [int]$sw.Elapsed.TotalMilliseconds + "ms")
}
function Stop-App([string]$settings) {
    Start-Process -FilePath $Exe -ArgumentList @('--exit', '--settings', "`"$settings`"") -Wait | Out-Null
    if (-not $script:appProc.WaitForExit(6000)) {
        Write-Host 'WARNING: the app did not respond to the exit signal, killing it'
        Stop-Process -Id $script:appProc.Id -Force
    }
}
function Start-Diag([int]$seconds, [string]$name) {
    Start-Process -FilePath $Exe -PassThru -ArgumentList @('--diag', '--seconds', "$seconds", '--interval', '100', '--csv', "`"$OutDir\$name.csv`"", '--out', "`"$OutDir\$name-diag.txt`"")
}
function Stop-TestChrome([string]$profileDir) {
    Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profileDir) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# ---------------- Phase 1: Brave (the user's real playback) ----------------
if (-not $SkipBravePhase) {
    $set1 = New-TestSettings (Join-Path $OutDir 'phase1') '[]'
    $d1 = Start-Diag 22 'phase1'
    Mark 'phase1-start'
    Start-App $set1
    Start-Sleep -Seconds 20
    Mark 'phase1-end'
    Stop-App $set1
    $d1.WaitForExit()
}

# ---------------- Phase 2: transitions (Brave is ignored so the user's music cannot disturb the test) ----------------
$set2 = New-TestSettings (Join-Path $OutDir 'phase2') '["brave"]'
$prof1 = Join-Path $OutDir 'chrome-profile-1'
$prof2 = Join-Path $OutDir 'chrome-profile-muted'
$d2 = Start-Diag 98 'phase2'
Mark 'phase2-start'
Start-App $set2
Start-Sleep -Seconds 12

Mark 'short-notification-0.5s'
(New-Object System.Media.SoundPlayer (Join-Path $assets 'blip05.wav')).PlaySync()
Start-Sleep -Milliseconds 5500

Mark 'chrome-10s-audio'
$common = @('--no-first-run', '--no-default-browser-check', '--autoplay-policy=no-user-gesture-required', '--disable-sync', '--disable-extensions', '--new-window')
Start-Process -FilePath $chrome -ArgumentList (@("--user-data-dir=`"$prof1`"") + $common + @($pageUrl)) | Out-Null
Start-Sleep -Seconds 27

Mark 'chrome-muted-tab'
Start-Process -FilePath $chrome -ArgumentList (@("--user-data-dir=`"$prof2`"", '--mute-audio') + $common + @($pageUrl)) | Out-Null
Start-Sleep -Seconds 15

Mark 'other-app-8s-audio'
(New-Object System.Media.SoundPlayer (Join-Path $assets 'noise8s.wav')).PlaySync()
Mark 'other-app-done'
Start-Sleep -Seconds 14

Mark 'phase2-end'
Stop-App $set2
Stop-TestChrome $prof1
Stop-TestChrome $prof2
$d2.WaitForExit()

$events | Set-Content -Path (Join-Path $OutDir 'events.csv') -Encoding ASCII
Write-Host "Results: $OutDir"
python (Join-Path $PSScriptRoot 'analyze.py') $OutDir
