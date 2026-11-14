@echo off
setlocal
set "DOTNET_EXE="
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
    "%USERPROFILE%\.dotnet\dotnet.exe" --list-sdks | findstr /r "^9\." >nul 2>&1
    if not errorlevel 1 set "DOTNET_EXE=%USERPROFILE%\.dotnet\dotnet.exe"
)
if not defined DOTNET_EXE (
    where dotnet >nul 2>&1
    if not errorlevel 1 (
        dotnet --list-sdks | findstr /r "^9\." >nul 2>&1
        if not errorlevel 1 set "DOTNET_EXE=dotnet"
    )
)
if not defined DOTNET_EXE (
    echo .NET 9 SDK not found. Install it and try again.
    goto :failed
)

echo Building SETBoard...
call "%DOTNET_EXE%" build "%~dp0SETBoard.csproj" -c Debug -o "%~dp0artifacts\quick-build" --nologo
if errorlevel 1 goto :failed
if not exist "%~dp0artifacts\quick-build\SETBoard.exe" goto :failed

echo.
echo Build succeeded: "%~dp0artifacts\quick-build\SETBoard.exe"
if /i not "%~1"=="--no-pause" pause
exit /b 0

:failed
echo.
echo Build failed. Check the errors above.
if /i not "%~1"=="--no-pause" pause
exit /b 1
