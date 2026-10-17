@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"

set "REPO_URL=https://github.com/TheMorgxx/DiskCleanerPro.git"

if not exist "DiskCleaner.sln" (
    echo [ERROR] Put this BAT in the DiskCleanerPro project root.
    goto :fail
)
where git >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Git is not installed or is not in PATH.
    goto :fail
)
if not exist ".git" (
    echo [ERROR] This folder has no Git repository. Use your existing project checkout.
    goto :fail
)
git symbolic-ref --quiet HEAD >nul
if errorlevel 1 (
    echo [ERROR] Detached HEAD. Switch to the branch you want to publish first.
    goto :fail
)

echo ==== Publish ALL project changes to GitHub ====
echo Repository: %REPO_URL%
echo Git ignore rules are respected. Tracked files are included.
echo.
git status --untracked-files=all
if errorlevel 1 goto :fail
echo.
choice /c YN /n /m "Commit and push these changes? [Y/N]: "
if errorlevel 2 goto :cancel
if not errorlevel 1 goto :cancel

git add --all
if errorlevel 1 goto :fail
git diff --cached --quiet --exit-code
if errorlevel 2 goto :fail
if not errorlevel 1 goto :push

git commit -m "Update DiskCleanerPro"
if errorlevel 1 (
    echo [ERROR] Commit failed. Check the error above and your Git user.name / user.email.
    goto :fail
)

:push
git push "%REPO_URL%" HEAD
if errorlevel 1 (
    echo [ERROR] Push failed. Check authentication or remote changes. No force push is used.
    goto :fail
)
echo.
echo [OK] Current branch published to GitHub.
pause
exit /b 0

:cancel
echo.
echo [CANCELLED] No commit or push performed.
pause
exit /b 0

:fail
echo.
echo [FAILED] Nothing was force-pushed. See the output above.
pause
exit /b 1
