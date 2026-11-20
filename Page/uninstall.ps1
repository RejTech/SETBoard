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

# Started from the in-app uninstall button, give the app time to finish exiting.
Write-Host '  Waiting 5 seconds before starting...' -ForegroundColor DarkGray
Start-Sleep -Seconds 5

# ------------------------------------------------------------- Stop processes
Write-Step 'Stopping SETBoard'
if (Get-Process SETBoard -ErrorAction SilentlyContinue) {
    # The watchdog restarts the main process unless it sees the allow-exit
    # signal, so signal first, then force-kill both processes.
    try {
        $allowExit = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'SETBoard.AllowExit')
        $allowExit.Set()
        $allowExit.Dispose()
    } catch { }

    cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
    Start-Sleep -Seconds 2
    # Second pass in case anything respawned before seeing the signal.
    cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
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

# Pop a final message so the window does not just disappear.
try {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    [void][System.Windows.Forms.MessageBox]::Show(
        "SETBoard has been uninstalled.`n`nYour saved whiteboard (.setb) files and the .NET 9 runtime were kept.",
        'SETBoard uninstaller',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information)
}
catch {
    try { Read-Host 'Uninstall complete. Press Enter to close' } catch { }
}
