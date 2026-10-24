@echo off
rem Pushes the whole project to GitHub (all logic is in tools\publish-github.ps1).
setlocal
cd /d "%~dp0"

where powershell >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Windows PowerShell was not found.
    goto :fail
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-github.ps1" %*
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
