# Resource test:
#  1) Leaks: session scanning runs ~30x faster than normal (5 ms); memory/handle/thread counts must not grow
#     over time (equivalent to hours of the normal ~150 ms scanning through the night).
#  2) CPU and memory use for 60 s with normal settings.
param([string]$Exe = "", [string]$OutDir = "", [int]$StressSeconds = 180, [int]$NormalSeconds = 60, [int]$SampleEvery = 15, [switch]$Silent)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'src\OrtamSesi\bin\Release\net8.0-windows\OrtamSesi.exe' }
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ('OrtamSesi-resources-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }

function Invoke-Phase([string]$name, [string]$json, [int]$seconds, [int]$sampleEvery) {
    $dir = Join-Path $OutDir $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $set = Join-Path $dir 'settings.json'
    [System.IO.File]::WriteAllText($set, $json)
    $p = Start-Process -FilePath $Exe -ArgumentList @('--settings', "`"$set`"") -PassThru
    Start-Sleep -Seconds 5
    $rows = @()
    $cpu0 = $null; $t0 = Get-Date
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) {
        $p.Refresh()
        if ($null -eq $cpu0) { $cpu0 = $p.TotalProcessorTime; $t0 = Get-Date }
        $rows += [pscustomobject]@{
            t_s = [int]$sw.Elapsed.TotalSeconds
            private_mb = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
            workingset_mb = [math]::Round($p.WorkingSet64 / 1MB, 1)
            handles = $p.HandleCount
            threads = $p.Threads.Count
            cpu_s = [math]::Round($p.TotalProcessorTime.TotalSeconds, 2)
        }
        Start-Sleep -Seconds $sampleEvery
    }
    $p.Refresh()
    $cpuPct = 100 * ($p.TotalProcessorTime - $cpu0).TotalSeconds / ((Get-Date) - $t0).TotalSeconds / [Environment]::ProcessorCount
    Start-Process -FilePath $Exe -ArgumentList @('--exit', '--settings', "`"$set`"") -Wait | Out-Null
    if (-not $p.WaitForExit(6000)) { Stop-Process -Id $p.Id -Force }
    Write-Host "=== $name ==="
    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    Write-Host ("Average CPU: {0:0.000}% (of all cores, {1} cores)" -f $cpuPct, [Environment]::ProcessorCount)
    $first = $rows[1]; $last = $rows[-1]
    Write-Host ("Change (first->last): private memory {0:+0.0;-0.0} MB, handles {1:+0;-0}, threads {2:+0;-0}" -f ($last.private_mb - $first.private_mb), ($last.handles - $first.handles), ($last.threads - $first.threads))
    Write-Host ''
}

# -Silent: everything runs normally but the sound is at -90 dB (inaudible), so long tests don't disturb the room
$levels = if ($Silent) { ', "MaxLevelDb": -90' } else { '' }
if ($StressSeconds -gt 0) {
    Invoke-Phase 'stress-5ms' ('{ "Playing": true, "Sound": "waterfall", "Volume": 1, "KeepPlayingWhenMuted": false, "WelcomeShown": true, "Advanced": { "PollMs": 5, "IgnoreApps": ["brave"]' + $levels + ' } }') $StressSeconds $SampleEvery
}
if ($NormalSeconds -gt 0) {
    Invoke-Phase 'normal' ('{ "Playing": true, "Sound": "waterfall", "Volume": 1, "KeepPlayingWhenMuted": false, "WelcomeShown": true, "Advanced": { "IgnoreApps": ["brave"]' + $levels + ' } }') $NormalSeconds 10
}
