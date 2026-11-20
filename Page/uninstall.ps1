# SETBoard uninstaller
# Usage (PowerShell):
#   irm https://rejtech.github.io/SETBoard/uninstall.ps1 | iex
#
# Steps: stop SETBoard and its watchdog -> remove the desktop and startup
# shortcuts -> delete the install directory. Saved whiteboard files and
# the .NET 9 runtime are left untouched.
# Output is English-only to stay safe under irm | iex on PowerShell 5.1.

$ErrorActionPreference = 'Stop'
$InstallDir = Join-Path $env:LOCALAPPDATA 'SETBoard'

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }

Write-Host ''
Write-Host '  SETBoard uninstaller' -ForegroundColor Yellow

# ------------------------------------------------------------- Stop processes
Write-Step 'Stopping SETBoard'
$running = Get-Process SETBoard -ErrorAction SilentlyContinue
if ($running) {
    # The watchdog restarts the main process unless it sees the allow-exit
    # signal, so signal first, then stop both processes.
    try {
        $allowExit = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'SETBoard.AllowExit')
        $allowExit.Set()
        $allowExit.Dispose()
    } catch { }

    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    # Second pass in case anything respawned before seeing the signal.
    Get-Process SETBoard -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Ok 'SETBoard and its watchdog stopped.'
}
else {
    Write-Ok 'SETBoard is not running.'
}

# ------------------------------------------------------------- Shortcuts
Write-Step 'Removing shortcuts'
$links = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'SETBoard.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Startup')) 'SETBoard.lnk')
)
foreach ($link in $links) {
    if (Test-Path $link) {
        Remove-Item $link -Force
        Write-Ok "Removed $link"
    }
}
Write-Ok 'Desktop shortcut removed, floating-bar auto-start disabled.'

# ------------------------------------------------------------- Program files
Write-Step 'Removing program files'
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Ok "Removed $InstallDir"
}
else {
    Write-Ok 'Install directory not found, nothing to remove.'
}

Write-Step 'Done'
Write-Host '  .NET 9 was left installed (other apps may depend on it).'
Write-Host '  Your saved whiteboard files were not touched.'
