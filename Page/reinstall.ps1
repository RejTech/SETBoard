# SETBoard reinstaller
# Usage (PowerShell):
#   irm https://rejtech.github.io/SETBoard/reinstall.ps1 | iex
# Also started by the in-app "Reinstall" button.
#
# Steps: stop SETBoard and its watchdog -> remove the shortcuts and the
# install directory silently -> download install.ps1 and run it, which
# rebuilds and reinstalls the latest version. Saved whiteboard files and
# the .NET 9 runtime are left untouched.
# Output is English-only to stay safe under irm | iex on PowerShell 5.1.

$ErrorActionPreference = 'Stop'
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$InstallDir = Join-Path $env:LOCALAPPDATA 'SETBoard'
$InstallScriptUrls = @(
    'https://rejtech.github.io/SETBoard/install.ps1',
    'https://v4.gh-proxy.org/https://raw.githubusercontent.com/RejTech/SETBoard/main/Page/install.ps1',
    'https://raw.githubusercontent.com/RejTech/SETBoard/main/Page/install.ps1',
    'https://raw.githubusercontent.com/RejTech/SETBoard/master/Page/install.ps1'
)

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  [!] $msg" -ForegroundColor Yellow }

Write-Host ''
Write-Host '  SETBoard reinstaller' -ForegroundColor Yellow

# Started from the in-app reinstall button, give the app time to finish exiting.
Write-Host '  Waiting 5 seconds before starting...' -ForegroundColor DarkGray
Start-Sleep -Seconds 5

# ------------------------------------------------------------- Stop processes
Write-Step 'Stopping SETBoard'
# The watchdog restarts the main process unless it sees the allow-exit
# signal, so signal first, then force-kill both processes.
try {
    $allowExit = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'SETBoard.AllowExit')
    $allowExit.Set()
    $allowExit.Dispose()
} catch { }
cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
Start-Sleep -Seconds 2
cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
Write-Ok 'SETBoard and its watchdog stopped.'

# ------------------------------------------------------------- Remove old copy
Write-Step 'Removing the current installation'
$links = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'SETBoard.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Startup')) 'SETBoard.lnk')
)
foreach ($link in $links) {
    if (Test-Path $link) {
        Remove-Item $link -Force -ErrorAction SilentlyContinue
        Write-Ok "Removed $link"
    }
}

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) {
        # File handles may need a moment to be released after the kill.
        Start-Sleep -Seconds 2
        Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Ok "Removed $InstallDir"
}
else {
    Write-Ok 'Install directory not found, nothing to remove.'
}

# ------------------------------------------------------------- Run installer
Write-Step 'Downloading the installer'
$installScript = Join-Path $env:TEMP 'SETBoard-Install.ps1'
$downloaded = $false
foreach ($url in $InstallScriptUrls) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $installScript -UseBasicParsing -TimeoutSec 30
        $text = Get-Content -Path $installScript -Raw
        if ($text -and $text.Contains('SETBoard')) { $downloaded = $true; break }
    }
    catch { Write-Warn 'Installer download failed, trying the next mirror...' }
}
if (-not $downloaded) {
    throw 'Could not download install.ps1. Reinstall manually from https://rejtech.github.io/SETBoard/.'
}
Write-Ok 'Installer downloaded.'

Write-Step 'Installing the latest version'
& $installScript
