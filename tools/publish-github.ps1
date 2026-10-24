# Commits the whole project (respecting .gitignore) and pushes it to GitHub.
# Usage: 1_publish_github.bat   or   powershell -File tools\publish-github.ps1 [-Message "text"] [-Yes]
# ASCII-only on purpose (Windows PowerShell 5.1).

[CmdletBinding()]
param(
    [string]$Message = 'Update DiskCleanerPro',
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

try {
    Write-Host '==== Publish DiskCleanerPro sources to GitHub ====' -ForegroundColor Cyan
    Write-Host ('Repository: ' + (Get-RepoUrl))

    # Sign in with the GitHub CLI when it is available, so that "git push" works without extra setup.
    if (Test-Tool 'gh') {
        $st = Invoke-Native -File 'gh' -Arguments @('auth', 'status', '--hostname', 'github.com') -Quiet -AllowFail
        if ($st.Code -ne 0) { Initialize-GitHubCli -Yes:$Yes } else { Invoke-Native -File 'gh' -Arguments @('auth', 'setup-git', '--hostname', 'github.com') -Quiet -AllowFail | Out-Null }
    }

    $result = Publish-ToGitHub -Message $Message -Yes:$Yes
    Write-Host ''
    Write-Ok ('Published commit ' + $result.Commit.Substring(0, 7) + ' to ' + $result.Url)
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('[FAILED] ' + $_.Exception.Message) -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    exit 1
}
