# Ortam Sesi - installer. No admin rights needed (installs for the current user only).
#  - Copies the program to %LOCALAPPDATA%\Programs\OrtamSesi
#  - Starts it automatically when Windows starts
#  - Adds a Start menu shortcut and an entry in "Settings > Apps" (so it can be uninstalled from there)
#  - Starts the program
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'dist\OrtamSesi.exe'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\OrtamSesi'
$exe = Join-Path $dest 'OrtamSesi.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OrtamSesi'
$version = '1.0.0'

function Stop-OrtamSesi {
    # Ask a running copy to exit (it fades out gently); force it if it does not respond
    foreach ($candidate in @($exe, $src)) {
        if (Test-Path $candidate) { Start-Process -FilePath $candidate -ArgumentList '--exit' -Wait -WindowStyle Hidden; break }
    }
    $deadline = (Get-Date).AddSeconds(6)
    while ((Get-Process OrtamSesi -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Get-Process OrtamSesi -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
}

try {
    Write-Host 'Installing Ortam Sesi...'
    if (-not (Test-Path $src)) { throw "Program file not found: $src" }
    if (-not (Test-Path (Join-Path $root 'dist\sounds\sounds.json'))) { throw 'Sounds folder not found: dist\sounds' }

    $desktopRt = Get-ChildItem -Path (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App') -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '8.*' }
    if (-not $desktopRt) {
        Write-Host ''
        Write-Host 'Ortam Sesi needs the free ".NET 8 Desktop Runtime" from Microsoft.' -ForegroundColor Yellow
        Write-Host 'The download page is opening: install ".NET Desktop Runtime 8" (x64), then run Install.cmd again.'
        Start-Process 'https://dotnet.microsoft.com/download/dotnet/8.0'
        throw '.NET 8 Desktop Runtime not found.'
    }

    Stop-OrtamSesi
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    # Program + sounds folder (cleared first so sounds removed in a newer version do not linger)
    Remove-Item -Path (Join-Path $dest 'sounds') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path (Join-Path $dest 'kaldir.ps1') -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root 'dist\*') -Destination $dest -Recurse -Force
    Copy-Item -Path (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $dest 'uninstall.ps1') -Force

    # Start with Windows
    New-Item -Path $runKey -Force -ErrorAction SilentlyContinue | Out-Null
    Set-ItemProperty -Path $runKey -Name 'OrtamSesi' -Value "`"$exe`" --autostart"

    # Start menu shortcut
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Ortam Sesi.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($lnk)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = $dest
    $sc.Description = 'Ortam Sesi: calm background sound'
    $sc.Save()

    # Show up in Settings > Apps so it can be uninstalled from there
    New-Item -Path $uninstKey -Force | Out-Null
    $props = @{
        DisplayName     = 'Ortam Sesi'
        DisplayVersion  = $version
        DisplayIcon     = $exe
        InstallLocation = $dest
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
        Publisher       = 'Ortam Sesi'
    }
    foreach ($k in $props.Keys) { Set-ItemProperty -Path $uninstKey -Name $k -Value $props[$k] }
    Set-ItemProperty -Path $uninstKey -Name 'NoModify' -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstKey -Name 'NoRepair' -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstKey -Name 'EstimatedSize' -Value ([int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)) -Type DWord

    Start-Process -FilePath $exe
    Write-Host ''
    Write-Host 'Installed and started.' -ForegroundColor Green
    Write-Host 'Click the moon icon next to the clock to pause, pick a sound or change the level.'
    Write-Host 'It starts automatically every time Windows starts.'
    Start-Sleep -Seconds 6
}
catch {
    Write-Host ''
    Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}
