# Installs the PiScreen display driver into SimHub as shared driver "PiScreen":
#   <SimHub>\SharedDrivers\PiScreen\DisplayDriver.exe
# Usage (from the unzipped folder):  powershell -ExecutionPolicy Bypass -File .\install.ps1 [-SimHubPath 'D:\SimHub']
param([string]$SimHubPath)
$ErrorActionPreference = 'Stop'

if (-not $SimHubPath) {
    $keys = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    $entry = Get-ItemProperty $keys -ErrorAction SilentlyContinue |
             Where-Object { $_.DisplayName -like 'SimHub*' -and $_.InstallLocation } | Select-Object -First 1
    $SimHubPath = if ($entry) { $entry.InstallLocation } else { Join-Path ${env:ProgramFiles(x86)} 'SimHub' }
}
if (-not (Test-Path (Join-Path $SimHubPath 'SimHubWPF.exe'))) {
    throw "SimHub not found in '$SimHubPath'. Re-run with -SimHubPath 'X:\Path\To\SimHub'."
}

# SimHub usually lives under Program Files: copying there needs admin rights.
$me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`" -SimHubPath `"$SimHubPath`""
    exit
}

$dest = Join-Path $SimHubPath 'SharedDrivers\PiScreen'
# SimHub restarts the driver on its own after it exits.
Get-Process DisplayDriver -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest\*" } | Stop-Process -Force
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item -Recurse -Force (Join-Path $PSScriptRoot 'PiScreen\*') $dest
# Zip downloads carry "Mark of the Web"; .NET Framework refuses to load marked DLLs.
Get-ChildItem $dest -Recurse | Unblock-File
Write-Host "PiScreen driver installed to $dest" -ForegroundColor Green
Write-Host "Next: SimHub > Devices > Device Builder, add an 'MMF Rendering' screen using shared driver 'PiScreen' (see README)."
