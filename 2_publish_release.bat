@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"

set "REPO=TheMorgxx/DiskCleanerPro"
set "REPO_URL=https://github.com/TheMorgxx/DiskCleanerPro.git"

if not exist "DiskCleaner.sln" (
    echo [ERROR] Put this BAT in the DiskCleanerPro project root.
    goto :fail
)
if not exist ".git" (
    echo [ERROR] Use the existing Git checkout and publish it with 1_publish_github.bat first.
    goto :fail
)
where git >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Git is not installed or is not in PATH.
    goto :fail
)
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Install the .NET 8 SDK first.
    goto :fail
)
where gh >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Install GitHub CLI: winget install --id GitHub.cli --exact
    echo Then open a new terminal and run: gh auth login --hostname github.com
    goto :fail
)
where powershell >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Windows PowerShell is required to unpack the source snapshot.
    goto :fail
)
gh auth status --hostname github.com >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Sign in first: gh auth login --hostname github.com
    goto :fail
)

git status --porcelain >nul
if errorlevel 1 goto :fail
set "DIRTY="
for /f "delims=" %%I in ('git status --porcelain') do set "DIRTY=1"
if defined DIRTY (
    echo [ERROR] There are uncommitted files. Run 1_publish_github.bat first.
    goto :fail
)
set "RELEASE_COMMIT="
for /f %%I in ('git rev-parse --verify HEAD') do set "RELEASE_COMMIT=%%I"
if not defined RELEASE_COMMIT goto :fail
gh api "repos/%REPO%/commits/%RELEASE_COMMIT%" --jq .sha >nul
if errorlevel 1 (
    echo [ERROR] This commit is not available on GitHub. Run 1_publish_github.bat first.
    goto :fail
)

set "RELEASE_TAG="
set /p "RELEASE_TAG=New release tag, for example v1.0.0: "
powershell -NoProfile -Command "if ($env:RELEASE_TAG -cmatch '^[vV]?[0-9][A-Za-z0-9._-]*$') { exit 0 } else { exit 1 }"
if errorlevel 1 (
    echo [ERROR] Use a version tag such as v1.0.0 or v1.0.0-beta.1.
    goto :fail
)
git check-ref-format "refs/tags/%RELEASE_TAG%"
if errorlevel 1 goto :fail
git ls-remote --exit-code --tags "%REPO_URL%" "refs/tags/%RELEASE_TAG%" >nul
set "TAG_RESULT=%ERRORLEVEL%"
if "%TAG_RESULT%"=="0" (
    echo [ERROR] This tag already exists. Choose a new version; existing releases are not overwritten.
    goto :fail
)
if not "%TAG_RESULT%"=="2" (
    echo [ERROR] Could not check the remote tag. Check network access and Git authentication.
    goto :fail
)

set "DISKCLEANER_RELEASE_WORK=%TEMP%\DiskCleanerRelease-%RANDOM%-%RANDOM%"
if exist "%DISKCLEANER_RELEASE_WORK%" (
    echo [ERROR] Temporary folder already exists. Run the BAT again.
    goto :fail
)
mkdir "%DISKCLEANER_RELEASE_WORK%"
if errorlevel 1 goto :fail
git archive --format=zip --output="%DISKCLEANER_RELEASE_WORK%\source.zip" HEAD
if errorlevel 1 goto :fail
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Expand-Archive -LiteralPath (Join-Path $env:DISKCLEANER_RELEASE_WORK 'source.zip') -DestinationPath (Join-Path $env:DISKCLEANER_RELEASE_WORK 'source')"
if errorlevel 1 goto :fail

echo.
echo ==== Build the solution from the published commit ====
dotnet build "%DISKCLEANER_RELEASE_WORK%\source\DiskCleaner.sln" -c Release
if errorlevel 1 (
    echo [ERROR] Build failed. Release will not be published.
    goto :fail
)

echo.
echo ==== Run tests before publishing the Release ====
dotnet test "%DISKCLEANER_RELEASE_WORK%\source\DiskCleaner.sln" -c Release --no-build --no-restore
if errorlevel 1 (
    echo [ERROR] Tests failed. Release will not be published.
    goto :fail
)

echo.
echo ==== Build a standalone Windows x64 EXE from the published commit ====
dotnet publish "%DISKCLEANER_RELEASE_WORK%\source\src\DiskCleaner.App\DiskCleaner.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o "%DISKCLEANER_RELEASE_WORK%\publish"
if errorlevel 1 goto :fail
if not exist "%DISKCLEANER_RELEASE_WORK%\publish\DiskCleaner.exe" (
    echo [ERROR] Publish succeeded but DiskCleaner.exe was not found.
    goto :fail
)

echo.
echo ==== Check publication files for symbols and private profile names ====
set "DISKCLEANER_RELEASE_SCRIPT=%~f0"
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; try { $text=[IO.File]::ReadAllText($env:DISKCLEANER_RELEASE_SCRIPT); $marker=':__DISKCLEANER_PRIVACY_POWERSHELL__'; $start=$text.LastIndexOf($marker,[StringComparison]::Ordinal); if ($start -lt 0) { throw 'Privacy check code not found.' }; & ([ScriptBlock]::Create($text.Substring($start+$marker.Length))) } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }"
if errorlevel 1 goto :fail

echo.
echo ==== Create GitHub Release and upload DiskCleaner.exe ====
gh release create "%RELEASE_TAG%" "%DISKCLEANER_RELEASE_WORK%\publish\DiskCleaner.exe" --repo "%REPO%" --target "%RELEASE_COMMIT%" --title "DiskCleanerPro %RELEASE_TAG%" --notes "Standalone Windows x64 executable. Built from commit %RELEASE_COMMIT%."
if errorlevel 1 goto :fail
echo.
echo [OK] Release created and DiskCleaner.exe uploaded.
echo Local output: "%DISKCLEANER_RELEASE_WORK%\publish\DiskCleaner.exe"
pause
exit /b 0

:fail
echo.
echo [FAILED] See the output above. Existing releases were not overwritten.
if defined DISKCLEANER_RELEASE_WORK echo Temporary files: "%DISKCLEANER_RELEASE_WORK%"
pause
exit /b 1

rem Embedded PowerShell. Batch execution always exits before this section.
:__DISKCLEANER_PRIVACY_POWERSHELL__
$ErrorActionPreference = 'Stop'

function Get-ForbiddenTerms {
    $terms = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $candidates = @($env:USERNAME)
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $profilePath = $env:USERPROFILE.TrimEnd([char[]]@('\', '/'))
        $candidates += [IO.Path]::GetFileName($profilePath)
    }
    $candidates += @($env:DISKCLEANER_EXTRA_FORBIDDEN -split ';')
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            [void]$terms.Add($candidate.Trim())
        }
    }
    if ($terms.Count -eq 0) { throw 'The forbidden-string list is empty.' }
    return @($terms)
}

function Test-FileContainsForbiddenTerms {
    param([string]$Path, [string[]]$Terms)
    if ($null -eq $Terms -or $Terms.Count -eq 0) { throw 'The forbidden-string list is empty.' }
    $longest = 0
    foreach ($term in $Terms) {
        if ([string]::IsNullOrWhiteSpace($term)) { throw 'An empty forbidden string is not allowed.' }
        $longest = [Math]::Max($longest, $term.Length)
    }
    $passes = @(
        @{ Encoding = [Text.Encoding]::UTF8; Offset = 0 },
        @{ Encoding = [Text.Encoding]::Unicode; Offset = 0 },
        @{ Encoding = [Text.Encoding]::Unicode; Offset = 1 }
    )
    $stream = [IO.File]::OpenRead($Path)
    try {
        foreach ($pass in $passes) {
            $stream.Position = $pass.Offset
            $reader = [IO.StreamReader]::new($stream, $pass.Encoding, $false, 4096, $true)
            try {
                $buffer = [char[]]::new(32768)
                $tail = ''
                while (($count = $reader.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $text = $tail + [string]::new($buffer, 0, $count)
                    foreach ($term in $Terms) {
                        if ($text.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
                    }
                    $keep = [Math]::Min($longest - 1, $text.Length)
                    $tail = $text.Substring($text.Length - $keep)
                }
            } finally { $reader.Dispose() }
        }
        return $false
    } finally { $stream.Dispose() }
}

try {
    if ([string]::IsNullOrWhiteSpace($env:DISKCLEANER_RELEASE_WORK)) { throw 'Publication folder is not configured.' }
    $publishDirectory = Join-Path $env:DISKCLEANER_RELEASE_WORK 'publish'
    if (-not (Test-Path -LiteralPath $publishDirectory -PathType Container)) { throw 'Publication folder does not exist.' }
    $pdbFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force -File -ErrorAction Stop | Where-Object { $_.Extension -ieq '.pdb' })
    if ($pdbFiles.Count -gt 0) { throw 'PDB files found in the publication folder. Release blocked.' }
    $forbiddenTerms = @(Get-ForbiddenTerms)
    $executable = Join-Path $publishDirectory 'DiskCleaner.exe'
    if (Test-FileContainsForbiddenTerms -Path $executable -Terms $forbiddenTerms) {
        throw 'A forbidden profile string was found in the executable. Release blocked.'
    }
    Write-Output '[OK] No PDB files or forbidden profile strings found.'
    exit 0
} catch {
    [Console]::Error.WriteLine('[ERROR] ' + $_.Exception.Message)
    exit 1
}
