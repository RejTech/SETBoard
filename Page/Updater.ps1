# SETBoard updater (pre-built binary)
# The app downloads this file and runs it in a NEW PowerShell window, then
# exits itself. This script stops SETBoard (including its watchdog), downloads
# the latest release zip and restarts the app. Shortcuts are left untouched.
# Usage: called by AutoUpdater.cs with known version as $args[0]
# Output is English-only for PowerShell 5.1 compatibility.

$ErrorActionPreference = 'Stop'
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$GitHubRepo = 'RejTech/SETBoard'
$InstallDir = Join-Path $env:LOCALAPPDATA 'SETBoard'
$ExePath    = Join-Path $InstallDir 'SETBoard.exe'
$Work       = Join-Path $env:TEMP ('SETBoard-update-' + [Guid]::NewGuid().ToString('N'))

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  [!] $msg" -ForegroundColor Yellow }

# Streamed download with progress bar.
function Save-File($url, $path, $activity) {
    $client = $null
    try {
        Write-Host "  $url"
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(180)
        $response = $client.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "HTTP $([int]$response.StatusCode)" }

        $total = $response.Content.Headers.ContentLength
        $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $target = [System.IO.File]::Create($path)
        $buffer = New-Object byte[] 81920
        $received = 0L
        $lastPercent = -1

        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $target.Write($buffer, 0, $read)
            $received += $read
            if ($total -gt 0) {
                $percent = [int]($received * 100 / $total)
                if ($percent -ne $lastPercent) {
                    $lastPercent = $percent
                    $status = '{0}%  ({1:N1} MB / {2:N1} MB)' -f $percent, ($received / 1MB), ($total / 1MB)
                    Write-Progress -Activity $activity -Status $status -PercentComplete $percent
                }
            }
        }

        $target.Dispose()
        $source.Dispose()
        $response.Dispose()
        return $true
    }
    finally {
        Write-Progress -Activity $activity -Completed -ErrorAction SilentlyContinue
        if ($client) { $client.Dispose() }
    }
}

# Accept version from AutoUpdater.cs via command-line argument,
# or fall back to self-resolution for standalone use (install.ps1).
$version = if ($args.Count -gt 0 -and $args[0]) { $args[0] } else { Get-LatestVersion }
if (-not $version) { throw 'Could not resolve the latest release version.' }

function Get-LatestDownloadUrl($version) {
    $zipName = "SETBoard-v${version}-win-x64.zip"

    # Try gh-proxy mirror first (fast in China).
    $url = "https://gh-proxy.com/github.com/RejTech/SETBoard/releases/download/v$version/$zipName"
    try {
        $client = New-Object System.Net.Http.HttpClient
        $r = $client.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if ($r.IsSuccessStatusCode) { $r.Dispose(); $client.Dispose(); return $url }
        $r.Dispose(); $client.Dispose()
    } catch { }

    # Try jsDelivr mirror.
    $url = "https://cdn.jsdelivr.net/gh/RejTech/SETBoard@v$version/$zipName"
    try {
        $client = New-Object System.Net.Http.HttpClient
        $r = $client.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if ($r.IsSuccessStatusCode) { $r.Dispose(); $client.Dispose(); return $url }
        $r.Dispose(); $client.Dispose()
    } catch { }

    # Fallback to GitHub direct.
    Write-Warn "Using GitHub direct download for: $zipName"
    return "https://github.com/RejTech/SETBoard/releases/download/v$version/$zipName"
}

try {
    Write-Host ''
    Write-Host '  SETBoard updater (pre-built binary)' -ForegroundColor Yellow

    # --------------------------------------------------------- Stop app
    Write-Step 'Stopping SETBoard'
    try {
        $allowExit = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'SETBoard.AllowExit')
        $allowExit.Set()
        $allowExit.Dispose()
    } catch { }
    cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
    Start-Sleep -Seconds 2
    cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
    Write-Ok 'SETBoard stopped.'

    # --------------------------------------------------------- Download & Install
    Write-Step "Downloading SETBoard v$version"
    $downloadUrl = Get-LatestDownloadUrl $version
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    $zip = Join-Path $Work 'SETBoard.zip'

    if (-not (Save-File $downloadUrl $zip ('Downloading SETBoard v' + $version))) {
        throw "Download failed. Check your network and try again."
    }
    Write-Ok "Downloaded v$version"

    Expand-Archive -Path $zip -DestinationPath $Work -Force
    $extracted = Get-ChildItem $Work -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'SETBoard.exe') } | Select-Object -First 1
    if (-not $extracted) { $extracted = [PSCustomObject]@{ FullName = $Work } }

    Write-Step 'Installing'
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item (Join-Path $extracted.FullName '*') $InstallDir -Recurse -Force
    Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zip -ErrorAction SilentlyContinue
    Write-Ok "Installed to $ExePath"

    # --------------------------------------------------------- Restart
    Write-Step 'Restarting'
    Start-Process $ExePath
    Write-Ok "Update complete. SETBoard v$version restarted."
    Start-Sleep -Seconds 3
}
catch {
    Write-Host ''
    Write-Host "  Update failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '  Retry from the app, or update manually:'
    Write-Host "  https://github.com/$GitHubRepo/releases/latest"
    Read-Host 'Press Enter to close'
    exit 1
}
