# SETBoard updater (build from source)
# The app downloads this file and runs it in a NEW PowerShell window, then
# exits itself. This script stops SETBoard (including its watchdog), builds
# the latest source and restarts the app. Shortcuts are left untouched.
# Output is English-only for PowerShell 5.1 compatibility.

$ErrorActionPreference = 'Stop'
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$GitHubRepo = 'RejTech/SETBoard'
$GiteeRepo  = 'rejstudios/SETBoard'
$InstallDir = Join-Path $env:LOCALAPPDATA 'SETBoard'
$ExePath    = Join-Path $InstallDir 'SETBoard.exe'
$Work       = Join-Path $env:TEMP ('SETBoard-update-' + [Guid]::NewGuid().ToString('N'))

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  [!] $msg" -ForegroundColor Yellow }

function Find-DotNet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @(
        "$env:USERPROFILE\.dotnet\dotnet.exe",
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        'C:\Program Files\dotnet\dotnet.exe')) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Test-DotNet9Sdk($exe) {
    if (-not $exe) { return $false }
    $sdks = & $exe --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match '^9\.' })
}

function Get-LatestVersion {
    Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
    # 1) github.com /releases/latest redirect — accurate, no api.github.com.
    # (gh-proxy/jsDelivr do NOT proxy release pages or the API: verified 403/timeout.)
    $client = $null
    try {
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.AllowAutoRedirect = $false
        $client = New-Object System.Net.Http.HttpClient($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(15)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('SETBoard')
        $r = $client.GetAsync("https://github.com/$GitHubRepo/releases/latest").GetAwaiter().GetResult()
        $loc = $r.Headers.Location
        if ($loc -and $loc.ToString() -match '/releases/tag/([^/]+)') {
            return ($Matches[1] -replace '^[vV]', '')
        }
    }
    catch { }
    finally { if ($client) { $client.Dispose() } }

    # 2) Official API.
    $client = $null
    try {
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(15)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('SETBoard')
        $json = $client.GetStringAsync("https://api.github.com/repos/$GitHubRepo/releases/latest").GetAwaiter().GetResult()
        $release = $json | ConvertFrom-Json
        if ($release.tag_name) { return ($release.tag_name -replace '^[vV]', '') }
    }
    catch { }
    finally { if ($client) { $client.Dispose() } }

    # 3) Gitee mirror tags — reachable in China; may lag behind GitHub.
    $client = $null
    try {
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(15)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('SETBoard')
        $json = $client.GetStringAsync("https://gitee.com/api/v5/repos/$GiteeRepo/tags").GetAwaiter().GetResult()
        $tags = $json | ConvertFrom-Json
        $latest = $tags |
            Where-Object { $_.name -match '^v?\d+\.\d+\.\d+$' } |
            ForEach-Object { [PSCustomObject]@{ Name = $_.name; Ver = [version]($_.name -replace '^v', '') } } |
            Sort-Object Ver -Descending | Select-Object -First 1
        if ($latest) {
            Write-Warn 'Using a Gitee mirror tag; it may lag behind GitHub.'
            return ($latest.Name -replace '^[vV]', '')
        }
    }
    catch { }
    finally { if ($client) { $client.Dispose() } }

    return $null
}

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

try {
    Write-Host ''
    Write-Host '  SETBoard updater (build from source)' -ForegroundColor Yellow

    # --------------------------------------------------------- .NET 9 SDK
    Write-Step 'Checking .NET 9 SDK'
    $script:DotNet = Find-DotNet
    if (Test-DotNet9Sdk $script:DotNet) {
        Write-Ok ".NET 9 SDK found: $($script:DotNet)"
    }
    else {
        Write-Host '  Installing .NET 9 SDK via the official dotnet-install script...'
        $di = Join-Path $env:TEMP 'dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $di -UseBasicParsing
        $dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $di `
            -Channel 9.0 -InstallDir $dotnetDir -NoPath
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($userPath -notlike "*$dotnetDir*") {
            [Environment]::SetEnvironmentVariable('Path', "$userPath;$dotnetDir", 'User')
        }
        $script:DotNet = Find-DotNet
        if (-not (Test-DotNet9Sdk $script:DotNet)) {
            throw '.NET 9 SDK is required to build. Install it from https://dotnet.microsoft.com/download/dotnet/9.0'
        }
        Write-Ok '.NET 9 SDK installed.'
    }

    # --------------------------------------------------------- Stop app
    Write-Step 'Stopping SETBoard'
    # Signal the watchdog not to restart the app, then stop both processes.
    try {
        $allowExit = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, 'SETBoard.AllowExit')
        $allowExit.Set()
        $allowExit.Dispose()
    } catch { }
    cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
    Start-Sleep -Seconds 2
    cmd /c 'taskkill /f /im SETBoard.exe >nul 2>&1'
    Write-Ok 'SETBoard stopped.'

    # --------------------------------------------------------- Download source
    Write-Step 'Downloading the source code'
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    Add-Type -AssemblyName System.Net.Http

    $sources = @(
        "https://gitee.com/$GiteeRepo/repository/archive/master.zip",
        "https://gitee.com/$GiteeRepo/repository/archive/main.zip",
        "https://github.com/$GitHubRepo/archive/refs/heads/main.zip",
        "https://github.com/$GitHubRepo/archive/refs/heads/master.zip"
    )

    $zip = Join-Path $Work 'src.zip'
    $downloaded = $false
    foreach ($url in $sources) {
        try {
            if (Save-File $url $zip 'Downloading SETBoard source') {
                if ((Get-Item $zip).Length -gt 100KB) { $downloaded = $true; break }
            }
        }
        catch { Write-Warn 'Source failed, trying the next mirror...' }
    }
    if (-not $downloaded) { throw 'Source download failed on all mirrors.' }
    Write-Ok 'Source downloaded.'

    # --------------------------------------------------------- Build
    Write-Step 'Building SETBoard (may take a few minutes)'
    Expand-Archive -Path $zip -DestinationPath $Work -Force
    $src = Get-ChildItem $Work -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'SETBoard.csproj') } |
        Select-Object -First 1
    if (-not $src) { throw 'SETBoard.csproj not found in the downloaded archive.' }

    # Match the latest Release version so the app does not offer the same
    # update again after restarting.
    $version = Get-LatestVersion
    if (-not $version) { Write-Warn 'Could not resolve the release version; keeping the repository version.json.' }

    if ($version) {
        Write-Ok "Build version: $version"
        [System.IO.File]::WriteAllText(
            (Join-Path $src.FullName 'version.json'),
            "{`"version`":`"$version`"}",
            (New-Object System.Text.UTF8Encoding($false)))
    }

    Push-Location $src.FullName
    try {
        & $script:DotNet publish SETBoard.csproj `
            -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=none `
            -o publish
        $buildCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($buildCode -ne 0) { throw "dotnet publish failed with exit code $buildCode." }

    $publish = Join-Path $src.FullName 'publish'
    if (-not (Test-Path (Join-Path $publish 'SETBoard.exe'))) { throw 'Build output SETBoard.exe not found.' }
    Write-Ok 'Build succeeded.'

    # --------------------------------------------------------- Install & restart
    Write-Step 'Installing and restarting'
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item (Join-Path $publish '*') $InstallDir -Recurse -Force
    Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue

    Start-Process $ExePath
    Write-Ok 'Update complete. SETBoard restarted.'
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
