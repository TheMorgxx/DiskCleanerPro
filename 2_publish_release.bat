@echo off
rem Builds DiskCleaner.exe and publishes a GitHub Release (all logic is in tools\publish-release.ps1).
rem Options: -Tag v1.0.0   -Yes   -DryRun   -SkipTests   -IgnorePrivacy
setlocal
cd /d "%~dp0"

where powershell >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Windows PowerShell was not found.
    goto :fail
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-release.ps1" %*
if errorlevel 1 goto :fail

echo.
echo ==== DONE ====
pause
exit /b 0

:fail
echo.
echo ==== FAILED - read the message above ====
pause
exit /b 1
