# Builds DiskCleaner.exe from the committed source and publishes it as a GitHub Release.
#
# Steps: sign in to GitHub -> push the sources -> pick a tag -> build + test + publish a standalone exe
#        -> privacy check -> create the Release -> upload the exe -> verify the upload.
#
# Usage: 2_publish_release.bat   or   powershell -File tools\publish-release.ps1 [-Tag v1.0.0] [-Yes]
#        [-DryRun]  build and check everything, but do not touch GitHub
#        [-SkipTests] [-IgnorePrivacy] [-Message "commit message"]
# ASCII-only on purpose (Windows PowerShell 5.1).

[CmdletBinding()]
param(
    [string]$Tag = '',
    [string]$Message = 'Update DiskCleanerPro',
    [switch]$Yes,
    [switch]$DryRun,
    [switch]$SkipTests,
    [switch]$IgnorePrivacy
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

$work = $null

try {
    Write-Host '==== Publish DiskCleanerPro Release ====' -ForegroundColor Cyan
    Set-Location -LiteralPath $script:ProjectRoot
    $url = Get-RepoUrl
    Write-Host ('Repository: ' + $url)
    if ($DryRun) { Write-Host 'DRY RUN: nothing will be pushed or released.' -ForegroundColor Yellow }

    # ------------------------------------------------------------ tools
    Write-Step 'Checking tools'
    if (-not (Test-Tool 'git')) { throw 'Git is not installed. Install Git for Windows: https://git-scm.com/download/win' }
    if (-not (Test-Tool 'dotnet')) { throw 'The .NET 8 SDK is not installed or not in PATH. Install it: https://dotnet.microsoft.com/download/dotnet/8.0' }
    $dv = Invoke-Native -File 'dotnet' -Arguments @('--version') -Quiet
    $dotnetVersion = ([string]$dv.Output[0]).Trim()
    if ($dotnetVersion -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 8) { throw ('.NET SDK 8 or newer is required (found: ' + $dotnetVersion + ').') }
    Write-Ok ('git, dotnet ' + $dotnetVersion)

    $slug = ''
    if (-not $DryRun) {
        $slug = Get-RepoSlug
        Initialize-GitHubCli -Yes:$Yes
        Write-Ok 'GitHub CLI is ready and signed in.'
    }

    # ------------------------------------------------------------ sources on GitHub
    if ($DryRun) {
        $commit = Get-GitValue @('rev-parse', '--verify', 'HEAD')
        if (-not $commit) { throw 'The repository has no commits yet. Run 1_publish_github.bat first.' }
        $dirty = Invoke-Git -Arguments @('status', '--porcelain') -Quiet -AllowFail
        if ($dirty.Output.Count -gt 0) { Write-Warn 'You have uncommitted changes. The build uses the last commit only.' }
    }
    else {
        $pub = Publish-ToGitHub -Message $Message -Yes:$Yes
        $commit = $pub.Commit
    }

    # ------------------------------------------------------------ tag
    Write-Step 'Release tag'
    if ([string]::IsNullOrWhiteSpace($Tag)) {
        $suggest = 'v1.0.0'
        if (-not $DryRun) {
            $best = $null
            $tags = Invoke-Git -Arguments @('ls-remote', '--tags', '--refs', $url) -Quiet -AllowFail
            foreach ($line in $tags.Output) {
                if ([string]$line -match 'refs/tags/[vV]?(\d+)\.(\d+)\.(\d+)$') {
                    $cur = @([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
                    if ($null -eq $best) { $best = $cur }
                    else {
                        for ($i = 0; $i -lt 3; $i++) {
                            if ($cur[$i] -gt $best[$i]) { $best = $cur; break }
                            if ($cur[$i] -lt $best[$i]) { break }
                        }
                    }
                }
            }
            if ($null -ne $best) { $suggest = ('v{0}.{1}.{2}' -f $best[0], $best[1], ($best[2] + 1)) }
        }
        $answer = Read-Host ('New release tag [' + $suggest + ']')
        if ([string]::IsNullOrWhiteSpace($answer)) { $Tag = $suggest } else { $Tag = $answer.Trim() }
    }
    if ($Tag -notmatch '^[vV]?[0-9]+\.[0-9]+\.[0-9]+([-+.][0-9A-Za-z.-]+)?$') {
        throw ('Bad tag "' + $Tag + '". Use a version such as v1.0.0 or v1.0.0-beta.1.')
    }
    $fmt = Invoke-Git -Arguments @('check-ref-format', ('refs/tags/' + $Tag)) -Quiet -AllowFail
    if ($fmt.Code -ne 0) { throw ('"' + $Tag + '" is not a valid git tag name.') }
    $version = $Tag.TrimStart('v', 'V')
    $isPrerelease = $version.Contains('-')

    $mode = 'create'
    if (-not $DryRun) {
        $tagSha = ''
        $lt = Invoke-Git -Arguments @('ls-remote', '--tags', $url, ('refs/tags/' + $Tag), ('refs/tags/' + $Tag + '^{}')) -Quiet
        foreach ($line in $lt.Output) {
            $parts = ([string]$line).Split("`t")
            if ($parts.Count -lt 2) { continue }
            if ($parts[1].Trim().EndsWith('^{}')) { $tagSha = $parts[0].Trim() }
            elseif (-not $tagSha) { $tagSha = $parts[0].Trim() }
        }
        $rel = Invoke-Native -File 'gh' -Arguments @('release', 'view', $Tag, '--repo', $slug) -Quiet -AllowFail
        $releaseExists = ($rel.Code -eq 0)
        if ($tagSha) {
            if ($tagSha -ne $commit) {
                throw ('Tag ' + $Tag + ' already exists on GitHub and points to a different commit (' + $tagSha.Substring(0, 7) + '). Choose a new version.')
            }
            if ($releaseExists) {
                Write-Warn ('Release ' + $Tag + ' already exists for this commit.')
                if (-not (Confirm-Action 'Re-build and replace its files?' -Yes:$Yes)) { throw 'Cancelled.' }
                $mode = 'upload'
            }
            else { Write-Info ('Tag ' + $Tag + ' exists without a Release - the Release will be created for it.') }
        }
        elseif ($releaseExists) {
            throw ('A Release named ' + $Tag + ' already exists. Choose a new version.')
        }
    }
    Write-Ok ('Tag: ' + $Tag + '   Version: ' + $version + '   Commit: ' + $commit.Substring(0, 7))

    # ------------------------------------------------------------ build from the committed source
    Write-Step 'Preparing a clean copy of the committed source'
    $work = Join-Path ([IO.Path]::GetTempPath()) ('DiskCleanerRelease-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $work | Out-Null
    $zip = Join-Path $work 'source.zip'
    $src = Join-Path $work 'source'
    $pubDir = Join-Path $work 'publish'
    Invoke-Git -Arguments @('archive', '--format=zip', ('--output=' + $zip), $commit) -Quiet | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $src -Force
    $sln = Join-Path $src 'DiskCleaner.sln'
    $appProj = Join-Path (Join-Path (Join-Path $src 'src') 'DiskCleaner.App') 'DiskCleaner.App.csproj'
    if (-not (Test-Path -LiteralPath $sln)) { throw 'DiskCleaner.sln was not found in the committed source.' }
    if (-not (Test-Path -LiteralPath $appProj)) { throw 'DiskCleaner.App.csproj was not found in the committed source.' }

    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    Write-Step 'Build'
    $r = Invoke-Native -File 'dotnet' -Arguments @('build', $sln, '-c', 'Release', ('-p:Version=' + $version)) -Show -AllowFail
    if ($r.Code -ne 0) { throw 'Build failed - see the compiler errors above. Nothing was released.' }

    if ($SkipTests) { Write-Warn 'Tests skipped (-SkipTests).' }
    else {
        Write-Step 'Tests'
        $r = Invoke-Native -File 'dotnet' -Arguments @('test', $sln, '-c', 'Release', '--no-build') -Show -AllowFail
        if ($r.Code -ne 0) { throw 'Tests failed - see above. Nothing was released. (Use -SkipTests only if you know why they fail.)' }
    }

    Write-Step 'Publish standalone Windows x64 exe'
    $pubArgs = @('publish', $appProj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=false', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', ('-p:Version=' + $version), '-o', $pubDir)
    $r = Invoke-Native -File 'dotnet' -Arguments $pubArgs -Show -AllowFail
    if ($r.Code -ne 0) { throw 'dotnet publish failed - see the errors above. Nothing was released.' }

    $exe = Join-Path $pubDir 'DiskCleaner.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'The publish step finished, but DiskCleaner.exe was not produced.' }
    $exeSize = (Get-Item -LiteralPath $exe).Length
    if ($exeSize -lt 1MB) { throw ('DiskCleaner.exe is suspiciously small (' + $exeSize + ' bytes).') }
    Write-Ok ('DiskCleaner.exe: {0:N1} MB' -f ($exeSize / 1MB))

    # ------------------------------------------------------------ checks
    Write-Step 'Checking the exe for symbols and private paths'
    $pdbs = @(Get-ChildItem -LiteralPath $pubDir -Recurse -Force -File | Where-Object { $_.Extension -ieq '.pdb' })
    if ($pdbs.Count -gt 0) { throw 'PDB files were found in the publish folder. Release blocked.' }
    $hits = @(Find-PrivateStrings $exe)
    if ($hits.Count -gt 0) {
        Write-Warn 'The exe contains text that looks like your private data:'
        foreach ($h in $hits) { Write-Host ('   ' + $h) -ForegroundColor Yellow }
        if ($IgnorePrivacy) { Write-Warn 'Continuing because of -IgnorePrivacy.' }
        elseif ($Yes) { throw 'Private strings found in the exe. Release blocked (re-run without -Yes to decide interactively, or use -IgnorePrivacy).' }
        else {
            $a = Read-Host 'Publish it anyway? [y/N]'
            if ($a -notmatch '^\s*(y|yes)\s*$') { throw 'Release blocked because of private strings in the exe.' }
        }
    }
    else { Write-Ok 'No PDB files and no private paths found.' }

    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $shaFile = Join-Path $pubDir 'DiskCleaner.exe.sha256'
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($shaFile, ($hash + '  DiskCleaner.exe' + "`n"), $utf8)
    $notesFile = Join-Path $work 'notes.md'
    $notes = 'DiskCleanerPro ' + $Tag + "`n`n" +
        'Standalone Windows x64 build. The .NET runtime is included, nothing else needs to be installed.' + "`n" +
        'Download DiskCleaner.exe and run it. Administrator rights are needed only for some system categories.' + "`n`n" +
        'Built from commit ' + $commit + '.' + "`n" +
        'SHA-256 of DiskCleaner.exe: `' + $hash + '`' + "`n"
    [IO.File]::WriteAllText($notesFile, $notes, $utf8)

    if ($DryRun) {
        Write-Host ''
        Write-Ok 'Dry run finished. Nothing was published.'
        Write-Host ('Built exe: ' + $exe)
        $work = $null   # keep the folder so the result can be inspected
        exit 0
    }

    # ------------------------------------------------------------ GitHub Release
    Write-Step 'GitHub Release'
    Write-Info ('Repository: ' + $slug)
    Write-Info ('Tag:        ' + $Tag + $(if ($isPrerelease) { ' (pre-release)' } else { '' }))
    Write-Info ('Commit:     ' + $commit)
    Write-Info ('File:       DiskCleaner.exe ({0:N1} MB)' -f ($exeSize / 1MB))
    if (-not (Confirm-Action 'Create/update the GitHub Release now?' -Yes:$Yes)) { throw 'Cancelled. Nothing was released.' }

    if ($mode -eq 'create') {
        $createArgs = @('release', 'create', $Tag, '--repo', $slug, '--target', $commit, '--title', ('DiskCleanerPro ' + $Tag), '--notes-file', $notesFile)
        if ($isPrerelease) { $createArgs += '--prerelease' }
        Invoke-Native -File 'gh' -Arguments $createArgs -Show | Out-Null
    }
    Write-Info 'Uploading files (the exe is large, this can take a few minutes)...'
    Invoke-Native -File 'gh' -Arguments @('release', 'upload', $Tag, $exe, $shaFile, '--repo', $slug, '--clobber') -Show | Out-Null

    # ------------------------------------------------------------ verify
    Write-Step 'Verifying the Release on GitHub'
    $view = Invoke-Native -File 'gh' -Arguments @('release', 'view', $Tag, '--repo', $slug, '--json', 'assets,url,isDraft') -Quiet
    $info = ($view.Output -join "`n") | ConvertFrom-Json
    $asset = @($info.assets | Where-Object { $_.name -eq 'DiskCleaner.exe' }) | Select-Object -First 1
    if (-not $asset) { throw 'The Release exists, but DiskCleaner.exe is not attached to it. Run this script again with the same tag to retry the upload.' }
    if ([int64]$asset.size -ne [int64]$exeSize) { throw 'The uploaded file size does not match the local file. Run this script again with the same tag to retry the upload.' }
    if ($info.isDraft) { Invoke-Native -File 'gh' -Arguments @('release', 'edit', $Tag, '--repo', $slug, '--draft=false') -Show | Out-Null }

    Write-Host ''
    Write-Ok ('Release published: ' + $info.url)
    if ($work -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('[FAILED] ' + $_.Exception.Message) -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    if ($work) { Write-Host ('Temporary files kept in: ' + $work) -ForegroundColor DarkGray }
    exit 1
}
