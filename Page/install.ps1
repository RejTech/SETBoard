# SETBoard one-click installer
# Usage (PowerShell):
#   irm https://rejtech.github.io/SETBoard/install.ps1 | iex
#
# Steps: install .NET 9 -> download the latest SETBoard release ->
# create a desktop shortcut -> enable floating-bar auto-start.
# Output is English-only so the script stays safe when piped through irm | iex
# on Windows PowerShell 5.1 (no response charset = mojibake for CJK text).

$ErrorActionPreference = 'Stop'
$Repo       = 'RejTech/SETBoard'
$InstallDir = Join-Path $env:LOCALAPPDATA 'SETBoard'
$ExePath    = Join-Path $InstallDir 'SETBoard.exe'

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  [!] $msg" -ForegroundColor Yellow }

Write-Host ''
Write-Host '  SETBoard installer' -ForegroundColor Yellow
Write-Host '  Touch whiteboard for classrooms - https://github.com/RejTech/SETBoard'

# ---------------------------------------------------------------- .NET 9
Write-Step 'Checking .NET 9 runtime'
$hasDotnet9 = $false
try {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $runtimes = & dotnet --list-runtimes 2>$null
        if ($runtimes -match 'Microsoft\.NETCore\.App 9\.') { $hasDotnet9 = $true }
    }
} catch { }

if ($hasDotnet9) {
    Write-Ok '.NET 9 is already installed.'
}
else {
    $installed = $false

    if (Get-Command winget -ErrorAction SilentlyContinue) {
        try {
            Write-Host '  Installing .NET 9 Desktop Runtime via winget...'
            winget install --id Microsoft.DotNet.DesktopRuntime.9 -e --silent `
                --accept-source-agreements --accept-package-agreements
            if ($LASTEXITCODE -eq 0) { $installed = $true }
        } catch { }
    }

    if (-not $installed) {
        try {
            Write-Host '  Installing .NET 9 via the official dotnet-install script...'
            $di = Join-Path $env:TEMP 'dotnet-install.ps1'
            Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $di -UseBasicParsing
            $dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $di `
                -Channel 9.0 -Runtime windowsdesktop -InstallDir $dotnetDir -NoPath
            if ($LASTEXITCODE -eq 0) {
                $installed = $true
                $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
                if ($userPath -notlike "*$dotnetDir*") {
                    [Environment]::SetEnvironmentVariable('Path', "$userPath;$dotnetDir", 'User')
                }
            }
        } catch { }
    }

    if ($installed) { Write-Ok '.NET 9 installed.' }
    else { Write-Warn '.NET 9 setup skipped/failed. SETBoard is self-contained and runs without it.' }
}

# ---------------------------------------------------------------- Download
Write-Step 'Downloading the latest SETBoard'
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

if (Test-Path $ExePath) {
    try {
        $fs = [System.IO.File]::Open($ExePath, 'Open', 'ReadWrite', 'None')
        $fs.Close()
    }
    catch {
        throw 'SETBoard is running. Exit it first (More -> Exit SETBoard) or update from About, then re-run this installer.'
    }
}

$urls = @(
    "https://github.com/$Repo/releases/latest/download/SETBoard.exe",
    "https://v4.gh-proxy.org/https://github.com/$Repo/releases/latest/download/SETBoard.exe",
    "https://ghfast.top/https://github.com/$Repo/releases/latest/download/SETBoard.exe"
)

$tmp = Join-Path $env:TEMP 'SETBoard-setup.exe'
$downloaded = $false
foreach ($url in $urls) {
    try {
        Write-Host "  $url"
        Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing -TimeoutSec 180
        if ((Get-Item $tmp).Length -gt 1MB) { $downloaded = $true; break }
    }
    catch { Write-Warn 'Source failed, trying the next mirror...' }
}
if (-not $downloaded) {
    throw "Download failed. Get SETBoard.exe manually: https://github.com/$Repo/releases/latest"
}

Move-Item $tmp $ExePath -Force
Write-Ok "Installed to $ExePath"

# ---------------------------------------------------------------- Shortcuts
function New-Shortcut($path, $target, $arguments, $workdir) {
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($path)
    $sc.TargetPath = $target
    $sc.Arguments = $arguments
    $sc.WorkingDirectory = $workdir
    $sc.IconLocation = "$target,0"
    $sc.Save()
}

Write-Step 'Creating shortcuts'
$desktop = [Environment]::GetFolderPath('Desktop')
New-Shortcut (Join-Path $desktop 'SETBoard.lnk') $ExePath '' $InstallDir
Write-Ok 'Desktop shortcut created.'

$startup = [Environment]::GetFolderPath('Startup')
New-Shortcut (Join-Path $startup 'SETBoard.lnk') $ExePath '--float' $InstallDir
Write-Ok 'Auto-start enabled: floating bar on sign-in (--float).'
Write-Host '  Remove the SETBoard shortcut from shell:startup to disable auto-start.'

# ---------------------------------------------------------------- Launch
Write-Step 'Done'
Start-Process $ExePath
Write-Ok 'SETBoard launched. Enjoy your class!'
