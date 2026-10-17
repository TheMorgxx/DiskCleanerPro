@echo off
setlocal
cd /d "%~dp0"

set "SLN=DiskCleaner.sln"
set "CONFIG=Release"

echo ==== DiskCleaner build ====

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found in PATH. Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
    goto :fail
)

if not exist "%SLN%" (
    echo [ERROR] %SLN% not found next to this script: %~dp0
    goto :fail
)

echo.
echo [0/3] dotnet SDK version:
dotnet --version
if errorlevel 1 goto :fail

echo.
echo [1/3] Restore
dotnet restore "%SLN%"
if errorlevel 1 goto :fail

echo.
echo [2/3] Build (%CONFIG%)
dotnet build "%SLN%" -c %CONFIG% --no-restore
if errorlevel 1 goto :fail

if /i "%~1"=="notest" (
    echo.
    echo [3/3] Tests skipped: notest
    goto :done
)

echo.
echo [3/3] Tests
dotnet test "%SLN%" -c %CONFIG% --no-build --no-restore
if errorlevel 1 goto :fail

:done
echo.
echo ==== OK: build finished successfully ====
echo App: src\DiskCleaner.App\bin\%CONFIG%\
pause
exit /b 0

:fail
echo.
echo ==== FAILED: see the errors above ====
pause
exit /b 1