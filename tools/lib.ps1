# Shared helpers for tools\publish-github.ps1 and tools\publish-release.ps1.
# This file is ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less files as ANSI.
# Works on Windows PowerShell 5.1 and PowerShell 7+.

Set-StrictMode -Version 2.0

$script:ProjectRoot = Split-Path -Parent $PSScriptRoot
$script:DefaultRepoUrl = 'https://github.com/TheMorgxx/DiskCleanerPro.git'

# ---------------------------------------------------------------- output helpers

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host ('==== ' + $Text + ' ====') -ForegroundColor Cyan
}

function Write-Ok([string]$Text) { Write-Host ('[OK] ' + $Text) -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host ('[WARN] ' + $Text) -ForegroundColor Yellow }
function Write-Info([string]$Text) { Write-Host $Text }

function Confirm-Action([string]$Question, [switch]$Yes) {
    if ($Yes) { return $true }
    $answer = Read-Host ($Question + ' [y/N]')
    return ($answer -match '^\s*(y|yes)\s*$')
}

# ---------------------------------------------------------------- configuration

function Get-RepoUrl {
    if (-not [string]::IsNullOrWhiteSpace($env:DISKCLEANER_REPO_URL)) { return $env:DISKCLEANER_REPO_URL.Trim() }
    return $script:DefaultRepoUrl
}

# "owner/name" for gh --repo. DISKCLEANER_REPO_SLUG overrides (used by tests).
function Get-RepoSlug {
    if (-not [string]::IsNullOrWhiteSpace($env:DISKCLEANER_REPO_SLUG)) { return $env:DISKCLEANER_REPO_SLUG.Trim() }
    $url = Get-RepoUrl
    if ($url -match 'github\.com[:/]+([^/]+)/([^/]+?)(\.git)?/?$') { return ($Matches[1] + '/' + $Matches[2]) }
    throw ('Cannot get owner/name from the repository URL: ' + $url)
}

# ---------------------------------------------------------------- native commands

# Runs a native command. Returns @{ Code; Output } (stdout lines).
# -Show   : stream output to the console instead of capturing it.
# -Quiet  : hide stderr.
# -AllowFail : do not throw on a non-zero exit code.
function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$File,
        [string[]]$Arguments = @(),
        [switch]$Show,
        [switch]$Quiet,
        [switch]$AllowFail
    )
    $global:LASTEXITCODE = 0
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # native stderr must not become a terminating error in PS 5.1
    $lines = @()
    try {
        if ($Show) {
            & $File @Arguments | Out-Host
        }
        elseif ($Quiet) {
            $out = & $File @Arguments 2>$null
            if ($null -ne $out) { $lines = @($out) }
        }
        else {
            $out = & $File @Arguments
            if ($null -ne $out) { $lines = @($out) }
        }
        $code = $global:LASTEXITCODE
    }
    finally { $ErrorActionPreference = $old }
    if ($code -ne 0 -and -not $AllowFail) {
        throw ('Command failed (exit code {0}): {1} {2}' -f $code, $File, ($Arguments -join ' '))
    }
    return [pscustomobject]@{ Code = $code; Output = $lines }
}

function Invoke-Git {
    param([string[]]$Arguments, [switch]$Show, [switch]$Quiet, [switch]$AllowFail)
    return Invoke-Native -File 'git' -Arguments $Arguments -Show:$Show -Quiet:$Quiet -AllowFail:$AllowFail
}

# First output line of a git command, or '' when it fails or prints nothing.
function Get-GitValue([string[]]$Arguments) {
    $r = Invoke-Git -Arguments $Arguments -Quiet -AllowFail
    if ($r.Code -ne 0 -or $r.Output.Count -eq 0) { return '' }
    return ([string]$r.Output[0]).Trim()
}

function Test-Tool([string]$Name) {
    return ($null -ne (Get-Command $Name -ErrorAction SilentlyContinue))
}

function Update-PathFromRegistry {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @()
    if ($machine) { $parts += $machine }
    if ($user) { $parts += $user }
    if ($parts.Count -gt 0) { $env:Path = ($parts -join ';') }
}

# ---------------------------------------------------------------- GitHub CLI

function Initialize-GitHubCli {
    param([switch]$Yes)

    if (-not (Test-Tool 'gh')) {
        Write-Warn 'GitHub CLI (gh) is not installed. It is required to create a Release.'
        if (Test-Tool 'winget') {
            if (Confirm-Action 'Install GitHub CLI with winget now?' -Yes:$Yes) {
                Invoke-Native -File 'winget' -Arguments @('install', '--id', 'GitHub.cli', '--exact', '--silent',
                    '--accept-package-agreements', '--accept-source-agreements') -Show -AllowFail | Out-Null
                Update-PathFromRegistry
                $ghDir = Join-Path $env:ProgramFiles 'GitHub CLI'
                if ((-not (Test-Tool 'gh')) -and (Test-Path -LiteralPath $ghDir)) { $env:Path = $env:Path + ';' + $ghDir }
            }
        }
        if (-not (Test-Tool 'gh')) {
            throw 'GitHub CLI is missing. Install it from https://cli.github.com (or: winget install --id GitHub.cli), then run this script again.'
        }
    }

    $st = Invoke-Native -File 'gh' -Arguments @('auth', 'status', '--hostname', 'github.com') -Quiet -AllowFail
    if ($st.Code -ne 0) {
        Write-Warn 'You are not signed in to GitHub. Starting "gh auth login" (a browser window will open).'
        Invoke-Native -File 'gh' -Arguments @('auth', 'login', '--hostname', 'github.com', '--git-protocol', 'https', '--web') -Show -AllowFail | Out-Null
        $st = Invoke-Native -File 'gh' -Arguments @('auth', 'status', '--hostname', 'github.com') -Quiet -AllowFail
        if ($st.Code -ne 0) { throw 'GitHub sign-in did not complete. Run "gh auth login --hostname github.com" manually and try again.' }
    }

    # Lets plain "git push" use the same GitHub sign-in (changes your global git credential helper for github.com).
    Invoke-Native -File 'gh' -Arguments @('auth', 'setup-git', '--hostname', 'github.com') -Quiet -AllowFail | Out-Null
}

# ---------------------------------------------------------------- publish the source to GitHub

function Normalize-RemoteUrl([string]$Url) {
    if ([string]::IsNullOrWhiteSpace($Url)) { return '' }
    $u = $Url.Trim().TrimEnd('/')
    if ($u.EndsWith('.git')) { $u = $u.Substring(0, $u.Length - 4) }
    return $u.ToLowerInvariant()
}

# Commits everything (respecting .gitignore) and pushes the current branch to GitHub.
# Returns @{ Branch; Commit; Url }.
function Publish-ToGitHub {
    param([string]$Message = 'Update DiskCleanerPro', [switch]$Yes)

    $url = Get-RepoUrl
    Set-Location -LiteralPath $script:ProjectRoot

    if (-not (Test-Tool 'git')) { throw 'Git is not installed. Install Git for Windows: https://git-scm.com/download/win' }

    Write-Step 'Local repository'
    if (-not (Test-Path -LiteralPath (Join-Path $script:ProjectRoot '.git'))) {
        $top = Get-GitValue @('rev-parse', '--show-toplevel')
        if ($top) {
            throw ('This folder is inside another Git repository: ' + $top + '. Run the script from the DiskCleanerPro repository itself.')
        }
        Write-Info 'No .git folder found - creating a new repository (branch "main").'
        $init = Invoke-Git -Arguments @('init', '-b', 'main') -Quiet -AllowFail
        if ($init.Code -ne 0) {
            Invoke-Git -Arguments @('init') -Quiet | Out-Null
            Invoke-Git -Arguments @('symbolic-ref', 'HEAD', 'refs/heads/main') -Quiet | Out-Null
        }
    }

    $branch = Get-GitValue @('symbolic-ref', '--quiet', '--short', 'HEAD')
    if (-not $branch) { throw 'Detached HEAD. Switch to a branch first (for example: git switch main).' }
    Write-Info ('Branch: ' + $branch)

    # remote "origin"
    $origin = Get-GitValue @('remote', 'get-url', 'origin')
    if (-not $origin) {
        Invoke-Git -Arguments @('remote', 'add', 'origin', $url) -Quiet | Out-Null
        $origin = $url
    }
    $pushTarget = $url
    $useOrigin = ((Normalize-RemoteUrl $origin) -eq (Normalize-RemoteUrl $url))
    if ($useOrigin) { $pushTarget = 'origin' }
    else { Write-Warn ('Remote "origin" points to ' + $origin + ' - pushing directly to ' + $url + ' instead.') }

    # commit identity
    if (-not (Get-GitValue @('config', 'user.name'))) {
        $n = Read-Host 'Git user.name (shown on your commits)'
        if ([string]::IsNullOrWhiteSpace($n)) { throw 'Git user.name is required.' }
        Invoke-Git -Arguments @('config', 'user.name', $n.Trim()) -Quiet | Out-Null
    }
    if (-not (Get-GitValue @('config', 'user.email'))) {
        $e = Read-Host 'Git user.email (shown on your commits)'
        if ([string]::IsNullOrWhiteSpace($e)) { throw 'Git user.email is required.' }
        Invoke-Git -Arguments @('config', 'user.email', $e.Trim()) -Quiet | Out-Null
    }

    Write-Step 'Checking access to GitHub'
    $probe = Invoke-Git -Arguments @('ls-remote', '--heads', $url) -Quiet -AllowFail
    if ($probe.Code -ne 0) {
        throw ('Cannot read ' + $url + '. Check that the repository exists on GitHub, that you have access, and that you are signed in ("gh auth login"). The repository must be created on GitHub first (https://github.com/new).')
    }
    Write-Ok 'GitHub repository is reachable.'

    # Build output must never be committed (it bloats the repository).
    $tracked = Invoke-Git -Arguments @('ls-files') -Quiet -AllowFail
    $dirs = @()
    foreach ($f in $tracked.Output) {
        if ([string]$f -match '^((?:.*/)?(?:bin|obj))/') { $dirs += $Matches[1] }
    }
    $dirs = @($dirs | Sort-Object -Unique)
    if ($dirs.Count -gt 0) {
        Write-Warn ('Build folders are tracked by git and will be removed from the index (files stay on disk): ' + ($dirs -join ', '))
        $rmArgs = @('rm', '-r', '--cached', '-q', '--ignore-unmatch', '--') + $dirs
        Invoke-Git -Arguments $rmArgs -Quiet | Out-Null
    }

    Write-Step 'Commit'
    Invoke-Git -Arguments @('add', '--all') -Quiet | Out-Null
    $diff = Invoke-Git -Arguments @('diff', '--cached', '--quiet') -Quiet -AllowFail
    if ($diff.Code -eq 1) {
        $status = Invoke-Git -Arguments @('status', '--short') -Quiet
        $shown = 0
        foreach ($line in $status.Output) {
            if ($shown -ge 40) { Write-Info ('  ... and ' + ($status.Output.Count - 40) + ' more'); break }
            Write-Info ('  ' + $line)
            $shown++
        }
        if (-not (Confirm-Action 'Commit and push these changes?' -Yes:$Yes)) { throw 'Cancelled. Nothing was committed or pushed.' }
        Invoke-Git -Arguments @('commit', '-q', '-m', $Message) -Show | Out-Null
    }
    elseif ($diff.Code -ne 0) {
        throw 'git diff failed.'
    }
    else {
        Write-Info 'No new changes to commit.'
    }

    $head = Get-GitValue @('rev-parse', '--verify', 'HEAD')
    if (-not $head) { throw 'There is nothing to publish: the repository has no commits.' }

    Write-Step 'Synchronizing with GitHub'
    $remoteSha = ''
    $lsr = Invoke-Git -Arguments @('ls-remote', $url, ('refs/heads/' + $branch)) -Quiet
    if ($lsr.Output.Count -gt 0) { $remoteSha = ([string]$lsr.Output[0]).Split("`t")[0].Trim() }

    if ($remoteSha -and $remoteSha -ne $head) {
        Invoke-Git -Arguments @('fetch', '--quiet', $url, ('refs/heads/' + $branch)) -Show | Out-Null
        $remoteIsOld = Invoke-Git -Arguments @('merge-base', '--is-ancestor', $remoteSha, 'HEAD') -Quiet -AllowFail
        if ($remoteIsOld.Code -ne 0) {
            $localIsOld = Invoke-Git -Arguments @('merge-base', '--is-ancestor', 'HEAD', $remoteSha) -Quiet -AllowFail
            if ($localIsOld.Code -eq 0) {
                Write-Info 'GitHub is ahead of your local branch - fast-forwarding.'
                Invoke-Git -Arguments @('merge', '--ff-only', '--quiet', $remoteSha) -Show | Out-Null
            }
            else {
                $rebaseArgs = @('rebase')
                $base = Invoke-Git -Arguments @('merge-base', 'HEAD', $remoteSha) -Quiet -AllowFail
                if ($base.Code -ne 0) {
                    # Unrelated histories: typically a brand-new local project linked to a GitHub repo that already
                    # has its own first commit (README, LICENSE). Your local files win on conflicts; the GitHub
                    # history is kept and nothing is force-pushed.
                    Write-Info 'GitHub already has its own first commit (for example a README). Putting your project on top of it; your local files win on conflicts.'
                    $rebaseArgs += @('-X', 'theirs')
                }
                else {
                    Write-Info 'GitHub has commits that you do not have locally - replaying your commits on top of them (no force push).'
                }
                $rebaseArgs += $remoteSha
                $rb = Invoke-Git -Arguments $rebaseArgs -Show -AllowFail
                if ($rb.Code -ne 0) {
                    Invoke-Git -Arguments @('rebase', '--abort') -Quiet -AllowFail | Out-Null
                    throw 'Your local history conflicts with GitHub, so the changes could not be combined automatically. Nothing was pushed and your files are unchanged. Resolve it manually (git pull --rebase) or ask for help.'
                }
            }
            $head = Get-GitValue @('rev-parse', '--verify', 'HEAD')
        }
    }

    if ($remoteSha -eq $head) {
        Write-Info 'GitHub already has this commit.'
    }
    else {
        Write-Step 'Push'
        $pushArgs = @('push')
        if ($useOrigin) { $pushArgs += '-u' }
        $pushArgs += $pushTarget
        $pushArgs += ('HEAD:refs/heads/' + $branch)
        Invoke-Git -Arguments $pushArgs -Show | Out-Null
    }

    # verify
    $check = Invoke-Git -Arguments @('ls-remote', $url, ('refs/heads/' + $branch)) -Quiet
    $after = ''
    if ($check.Output.Count -gt 0) { $after = ([string]$check.Output[0]).Split("`t")[0].Trim() }
    if ($after -ne $head) { throw 'Push finished, but GitHub does not show the expected commit. Check the repository on GitHub.' }

    Write-Ok ('Branch "' + $branch + '" is on GitHub at ' + $head.Substring(0, 7) + '.')
    return [pscustomobject]@{ Branch = $branch; Commit = $head; Url = $url }
}

# ---------------------------------------------------------------- privacy scan of the release exe

$script:ScannerSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class DcScanner
{
    private static byte Fold(byte b) { return (b >= 65 && b <= 90) ? (byte)(b + 32) : b; }

    private static byte[] Prep(byte[] raw)
    {
        byte[] r = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++) r[i] = Fold(raw[i]);
        return r;
    }

    // Returns the needles that occur in the file (UTF-8 or UTF-16LE, ASCII case-insensitive).
    public static string[] Find(string path, string[] needles)
    {
        List<byte[]> pats = new List<byte[]>();
        List<string> labels = new List<string>();
        foreach (string n in needles)
        {
            if (string.IsNullOrEmpty(n)) continue;
            string[] variants = new string[] { n, n.ToLowerInvariant(), n.ToUpperInvariant() };
            foreach (string v in variants)
            {
                pats.Add(Prep(Encoding.UTF8.GetBytes(v))); labels.Add(n);
                pats.Add(Prep(Encoding.Unicode.GetBytes(v))); labels.Add(n);
            }
        }
        int max = 0;
        foreach (byte[] p in pats) { if (p.Length > max) max = p.Length; }
        Dictionary<string, bool> found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (max == 0) return new string[0];

        const int Chunk = 1 << 20;
        byte[] buf = new byte[Chunk + max];
        using (FileStream fs = File.OpenRead(path))
        {
            int carry = 0;
            while (true)
            {
                int read = fs.Read(buf, carry, Chunk);
                if (read <= 0) break;
                int len = carry + read;
                for (int pi = 0; pi < pats.Count; pi++)
                {
                    if (found.ContainsKey(labels[pi])) continue;
                    byte[] p = pats[pi];
                    byte first = p[0];
                    int last = len - p.Length;
                    for (int i = 0; i <= last; i++)
                    {
                        if (Fold(buf[i]) != first) continue;
                        int k = 1;
                        while (k < p.Length && Fold(buf[i + k]) == p[k]) k++;
                        if (k == p.Length) { found[labels[pi]] = true; break; }
                    }
                }
                carry = Math.Min(max - 1, len);
                Buffer.BlockCopy(buf, len - carry, buf, 0, carry);
            }
        }
        return new List<string>(found.Keys).ToArray();
    }
}
'@

# Looks for traces of your Windows profile ("Users\<name>") and of DISKCLEANER_EXTRA_FORBIDDEN (";"-separated) inside a file.
# Returns the list of strings that were found (empty = clean).
function Find-PrivateStrings([string]$Path) {
    if (-not ('DcScanner' -as [type])) { Add-Type -TypeDefinition $script:ScannerSource -Language CSharp }

    $needles = New-Object System.Collections.Generic.List[string]
    $names = @([Environment]::UserName)
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $names += [IO.Path]::GetFileName($env:USERPROFILE.TrimEnd('\', '/'))
    }
    foreach ($name in $names) {
        if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -lt 2) { continue }
        foreach ($n in @(('Users\' + $name), ('Users/' + $name))) {
            if (-not $needles.Contains($n)) { $needles.Add($n) }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:DISKCLEANER_EXTRA_FORBIDDEN)) {
        foreach ($x in ($env:DISKCLEANER_EXTRA_FORBIDDEN -split ';')) {
            $t = $x.Trim()
            if ($t.Length -ge 4) { if (-not $needles.Contains($t)) { $needles.Add($t) } }
            elseif ($t.Length -gt 0) { Write-Warn ('DISKCLEANER_EXTRA_FORBIDDEN entry "' + $t + '" is shorter than 4 characters and is ignored (too many false positives).') }
        }
    }
    if ($needles.Count -eq 0) { return @() }
    $hits = [DcScanner]::Find($Path, $needles.ToArray())
    return @($hits)
}
