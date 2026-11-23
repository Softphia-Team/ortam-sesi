# OPTIONAL: turns off (or restores) "Allow the computer to turn off this device to save power" for the
# Bluetooth adapter. While that is on, Windows may switch the adapter off when idle; the speaker loses its
# connection and switches itself off. Needs admin rights (Windows shows a confirmation prompt).
#   Turn power saving off (recommended): powershell -ExecutionPolicy Bypass -File bluetooth-power-saving.ps1
#   Restore the previous behavior:      powershell -ExecutionPolicy Bypass -File bluetooth-power-saving.ps1 -Restore
# Same setting by hand: Device Manager > Bluetooth > (adapter) > Properties > Power Management >
#   untick "Allow the computer to turn off this device to save power".
param([switch]$Restore)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Restore) { $args2 += '-Restore' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $args2
    exit
}

$enable = [bool]$Restore
# Bluetooth-class adapters connected over USB/PCI
$adapters = Get-PnpDevice -Class Bluetooth -PresentOnly | Where-Object { $_.InstanceId -match '^(USB|PCI)\\' }
if (-not $adapters) { Write-Host 'No Bluetooth adapter found.'; Read-Host 'Press Enter'; exit 1 }

foreach ($a in $adapters) {
    $wmi = Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable |
        Where-Object { $_.InstanceName -like ($a.InstanceId + '*') }
    if (-not $wmi) { Write-Host "$($a.FriendlyName): no power management setting (nothing to change)."; continue }
    foreach ($w in $wmi) {
        $w | Set-CimInstance -Property @{ Enable = $enable }
        Write-Host ("{0}: 'Allow the computer to turn off this device to save power' is now {1}." -f $a.FriendlyName, $(if ($enable) { 'ON (restored)' } else { 'OFF' }))
    }
}
Write-Host 'Done. The change takes effect immediately; no restart needed.'
Start-Sleep -Seconds 5
