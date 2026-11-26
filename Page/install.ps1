# SETBoard one-click installer (pre-built binary)
# Usage (PowerShell):
#   irm https://rejtech.github.io/SETBoard/install.ps1 | iex
#
# Steps: check .NET 9 runtime -> download zip from GitHub Releases
#       (gh-proxy / jsdelivr mirror first, GitHub fallback) -> extract ->
#       create shortcuts -> launch.
# Output is English-only so the script stays safe when piped through irm | iex
# on Windows PowerShell 5.1 (no response charset = mojibake for CJK text).

$ErrorActionPreference = 'Stop'
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$GitHubRepo = 'RejTech/SETBoard'

$InstallDir = Join-Path $env:LOCALAPPDATA 'SETBoard'
$ExePath    = Join-Path $InstallDir 'SETBoard.exe'
$Work       = Join-Path $env:TEMP ('SETBoard-extract-' + [Guid]::NewGuid().ToString('N'))

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  [!] $msg" -ForegroundColor Yellow }

# Download using built-in PowerShell cmdlet (no HttpClient needed).
function Save-File($url, $path, $activity) {
    try {
        Write-Host "  $url"
        Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing -TimeoutSec 180 | Out-Null
        return $true
    }
    catch {
        Write-Host "  [!] $_"
        return $false
    }
}

# Test URL availability using HEAD request.
function Test-Url($url) {
    try {
        $r = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 15 -ErrorAction Stop
        $r.Dispose()
        return $true
    } catch { return $false }
}

# Resolve the latest release version and download URL, trying mirrors in order.
function Get-LatestVersion {
    # 1) GitHub /releases/latest redirect — accurate, no api.github.com needed.
    try {
        $r = Invoke-WebRequest -Uri "https://github.com/$GitHubRepo/releases/latest" -UseBasicParsing -MaximumRedirection 0 -ErrorAction SilentlyContinue -OutFile $null
        $r.Dispose()
    } catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode -eq 302) {
            $loc = $_.Exception.Response.Headers['Location']
            if ($loc -match '/releases/tag/v(\d+\.\d+\.\d+)') { return $Matches[1] }
        } else { return $null }
    }

    # 2) Official API fallback.
    try {
        $json = (Invoke-WebRequest -Uri "https://api.github.com/repos/$GitHubRepo/releases/latest" -UseBasicParsing -TimeoutSec 15).Content
        $release = $json | ConvertFrom-Json
        if ($release.tag_name) { return ($release.tag_name -replace '^[vV]', '') }
    } catch { }

    return $null
}

function Get-LatestDownloadUrl {
    $version = Get-LatestVersion
    if (-not $version) { return $null }

    $zipName = "SETBoard-v${version}-win-x64.zip"

    # 1) gh-proxy mirror (fast in China)
    $url = "https://gh-proxy.com/github.com/RejTech/SETBoard/releases/download/v$version/$zipName"
    if (Test-Url $url) { Write-Ok "Using gh-proxy mirror for: $zipName"; return @{ Url = $url; Version = $version } }

    # 2) jsdelivr mirror
    $url = "https://cdn.jsdelivr.net/gh/RejTech/SETBoard@v$version/$zipName"
    if (Test-Url $url) { Write-Ok "Using jsdelivr mirror for: $zipName"; return @{ Url = $url; Version = $version } }

    # 3) GitHub direct (slowest, last resort)
    $url = "https://github.com/RejTech/SETBoard/releases/download/v$version/$zipName"
    Write-Warn "Using GitHub direct download for: $zipName"
    return @{ Url = $url; Version = $version }
}

Write-Host ''
Write-Host '  SETBoard installer (pre-built binary)' -ForegroundColor Yellow
Write-Host '  Touch whiteboard for classrooms - https://github.com/RejTech/SETBoard'

# ------------------------------------------------------------- Check .NET 9 Runtime
# The self-contained build bundles the runtime, but a quick check is still useful.
Write-Step 'Checking .NET 9 Runtime'
$dotnetExe = $null
foreach ($p in @(
    "$env:USERPROFILE\.dotnet\dotnet.exe",
    "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
    'C:\Program Files\dotnet\dotnet.exe')) {
    if (Test-Path $p) { $dotnetExe = $p; break }
}

if ($dotnetExe) {
    Write-Ok ".NET runtime found: $($dotnetExe)"
} else {
    Write-Warn '.NET 9 runtime not found — the self-contained build includes it, but having a system runtime is recommended.'
}

# ------------------------------------------------------------- Stop running app
$running = Get-Process SETBoard -ErrorAction SilentlyContinue
if ($running) {
    Write-Step 'Stopping the running SETBoard'
    try {
        $allowExit = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'SETBoard.AllowExit')
        $allowExit.Set()
        $allowExit.Dispose()
    } catch { }
    cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
    Start-Sleep -Seconds 2
    cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
    Write-Ok 'SETBoard stopped.'
}

# ------------------------------------------------------------- Download from Releases
Write-Step 'Downloading the latest release'

$download = Get-LatestDownloadUrl
if (-not $download) {
    throw "Could not resolve the latest release version. Check your network and try again."
}

New-Item -ItemType Directory -Force -Path $Work | Out-Null
$zip = Join-Path $Work 'SETBoard.zip'

if (-not (Save-File $download.Url $zip ('Downloading SETBoard v' + $download.Version))) {
    throw "Download failed. Check your network and try again."
}
Write-Ok "Downloaded v$($download.Version)"

# ------------------------------------------------------------- Extract & Install
Write-Step 'Installing'
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Expand-Archive -Path $zip -DestinationPath $Work -Force

# Find the extracted directory (may have nested structure)
$extracted = Get-ChildItem $Work -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'SETBoard.exe') } | Select-Object -First 1
if (-not $extracted) {
    # Fallback: use the work directory itself
    $extracted = [PSCustomObject]@{ FullName = $Work }
}

Copy-Item (Join-Path $extracted.FullName '*') $InstallDir -Recurse -Force
Write-Ok "Installed to $ExePath"

# ------------------------------------------------------------- Shortcuts
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

# ------------------------------------------------------------- Launch & cleanup
Write-Step 'Done'
Start-Process $ExePath
Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zip -ErrorAction SilentlyContinue
Write-Ok "SETBoard v$($download.Version) launched. Enjoy your class!"
