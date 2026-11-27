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

# Streamed HttpClient download with a throttled progress bar.
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

# Resolve the latest release download URL, trying mirrors in order.
function Get-LatestDownloadUrl {
    $version = Get-LatestVersion
    if (-not $version) { return $null }

    # Build mirror URLs for the zip asset
    $zipName = "SETBoard-v${version}-win-x64.zip"

    # 1) gh-proxy mirror (fast in China)
    $ghProxyUrl = "https://gh-proxy.com/github.com/RejTech/SETBoard/releases/download/v$version/$zipName"
    try {
        Write-Ok "Using gh-proxy mirror for: $zipName"
        return @{ Url = $ghProxyUrl; Version = $version }
    } catch {
        Write-Warn "gh-proxy failed, trying jsdelivr..."
    }

    # 2) jsDelivr mirror
    $jsdelivrUrl = "https://cdn.jsdelivr.net/gh/RejTech/SETBoard@v$version/$zipName"
    try {
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(30)
        $r = $client.GetAsync("https://cdn.jsdelivr.net/gh/RejTech/SETBoard@v$version/$zipName", [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if ($r.IsSuccessStatusCode) {
            $r.Dispose()
            $client.Dispose()
            Write-Ok "Using jsdelivr mirror for: $zipName"
            return @{ Url = $jsdelivrUrl; Version = $version }
        }
        $r.Dispose()
        $client.Dispose()
    } catch {
        Write-Warn "jsdelivr failed, falling back to GitHub direct..."
    }

    # 3) GitHub direct (slowest, last resort)
    $ghUrl = "https://github.com/RejTech/SETBoard/releases/download/v$version/$zipName"
    Write-Warn "Using GitHub direct download for: $zipName"
    return @{ Url = $ghUrl; Version = $version }
}

function Get-LatestVersion {
    # 1) GitHub /releases/latest redirect — accurate, no api.github.com needed.
    try {
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.AllowAutoRedirect = $false
        $client = New-Object System.Net.Http.HttpClient($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(15)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('SETBoard')
        $r = $client.GetAsync("https://github.com/$GitHubRepo/releases/latest").GetAwaiter().GetResult()
        $loc = $r.Headers.Location
        if ($loc -and $loc.ToString() -match '/releases/tag/v(\d+\.\d+\.\d+)') {
            $client.Dispose()
            return $Matches[1]
        }
        $client.Dispose()
    }
    catch { }

    # 2) Official API fallback.
    try {
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(15)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('SETBoard')
        $json = $client.GetStringAsync("https://api.github.com/repos/$GitHubRepo/releases/latest").GetAwaiter().GetResult()
        $release = $json | ConvertFrom-Json
        if ($release.tag_name) {
            $client.Dispose()
            return ($release.tag_name -replace '^[vV]', '')
        }
        $client.Dispose()
    }
    catch { }

    return $null
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
Add-Type -AssemblyName System.Net.Http

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
