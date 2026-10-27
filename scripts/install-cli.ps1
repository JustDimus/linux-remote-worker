<#
.SYNOPSIS
Installs, updates, checks or removes lrw - the command line of Linux Remote Worker - on Windows, together with its
MCP server and skill for Claude Code.

.DESCRIPTION
One line in PowerShell - no clone, no administrator rights, no .NET:

    irm https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.ps1 | iex

With options:

    & ([scriptblock]::Create((irm https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.ps1))) -Status

scripts/install-cli.sh does the same on macOS and Linux:
- lrw: the release build for this CPU, checked against the release's SHA256SUMS.txt, goes to
  %LOCALAPPDATA%\Programs\lrw and onto the user PATH. Run it again to update; a running lrw (an MCP server of an
  open Claude session) is renamed out of the way and keeps working until it restarts.
- Claude Code, when installed: lrw is registered as the MCP server "lrw" (this needs the claude command on PATH),
  and the release's skill package (lrw-skill.tar.gz, checked the same way) goes to
  %USERPROFILE%\.claude\skills\linux-remote-worker (CLAUDE_CONFIG_DIR is honoured). A skill put there some other way
  is left alone.
- -Uninstall removes all of that; saved servers and logs stay.
The newest version comes from releases/latest/download/version.txt, GitHub's permanent link to a file of the newest
release. Windows PowerShell 5.1 or PowerShell 7 on Windows 10 or newer.
#>
param(
    # Show what is installed and whether an update exists
    [switch]$Status,
    # Remove lrw, its PATH entry, its MCP registration and the skill (saved servers and logs stay)
    [switch]$Uninstall,
    # A release such as v0.1.0 instead of the newest
    [string]$Version = '',
    # Saved lrw profile the MCP server connects to by default
    [string]$McpProfile = '',
    # Do not register the MCP server
    [switch]$NoMcp,
    # Do not add the skill
    [switch]$NoSkill,
    # Leave PATH alone
    [switch]$NoPath,
    # Download again even when that version is installed
    [switch]$Force
)

# A child scope: run through iex, nothing here leaks into the caller's session, and an error never closes it
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Windows PowerShell draws download progress very slowly
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    $repo = 'JustDimus/linux-remote-worker'
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\lrw'
    $exe = Join-Path $installDir 'lrw.exe'
    $claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
    $skillDir = Join-Path $claudeDir 'skills\linux-remote-worker'
    $skillMark = '.installed-by-lrw-install-cli'
    $headers = @{ 'User-Agent' = 'lrw-install-cli' }

    # Output and exit code of a program, without its stderr turning into PowerShell errors
    function Invoke-Tool([string]$Path, [string[]]$Arguments) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = & $Path @Arguments 2>&1 | ForEach-Object { "$_" }
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n").Trim() }
        }
        finally { $ErrorActionPreference = $previous }
    }

    function Get-Claude {
        $command = Get-Command claude -ErrorAction SilentlyContinue
        if ($command) { $command.Source }
    }

    function Get-InstalledVersion {
        if (-not (Test-Path -LiteralPath $exe)) { return $null }
        $result = Invoke-Tool $exe @('version', '--text')
        if ($result.ExitCode -eq 0) { $result.Output }
    }

    function Get-LatestTag {
        try { $content = (Invoke-WebRequest -UseBasicParsing -Headers $headers -Uri "https://github.com/$repo/releases/latest/download/version.txt").Content }
        catch { throw "Could not ask GitHub for the newest release of ${repo}: $($_.Exception.Message)" }
        # Release files come as application/octet-stream, so the content arrives as bytes
        $version = $(if ($content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($content) } else { [string]$content }).Trim()
        if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "The newest release of $repo has no readable version.txt." }
        "v$version"
    }

    function Save-Url([string]$Uri, [string]$Path) {
        try { Invoke-WebRequest -UseBasicParsing -Headers $headers -Uri $Uri -OutFile $Path }
        catch { throw "Download failed: $Uri ($($_.Exception.Message))" }
    }

    # A file of the release in $Dir, checked against the release's SHA256SUMS.txt; returns its path
    function Save-ReleaseFile([string]$Tag, [string]$File, [string]$Dir) {
        $sums = Join-Path $Dir 'SHA256SUMS.txt'
        if (-not (Test-Path -LiteralPath $sums)) { Save-Url "https://github.com/$repo/releases/download/$Tag/SHA256SUMS.txt" $sums }
        $path = Join-Path $Dir $File
        Save-Url "https://github.com/$repo/releases/download/$Tag/$File" $path
        $expected = foreach ($line in Get-Content -LiteralPath $sums) {
            if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$' -and $Matches[2].Trim() -eq $File) { $Matches[1] }
        }
        if (-not $expected) { throw "$File is not listed in SHA256SUMS.txt of $Tag." }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne ([string]$expected).ToUpperInvariant()) {
            throw "$File does not match SHA256SUMS.txt of $Tag - nothing was installed."
        }
        $path
    }

    # The user PATH is read and written raw, so entries like %USERPROFILE%\bin stay unexpanded
    function Get-UserPath {
        $key = Get-Item -LiteralPath 'HKCU:\Environment'
        $kind = if ($key.GetValueNames() -contains 'Path') { $key.GetValueKind('Path') } else { [Microsoft.Win32.RegistryValueKind]::ExpandString }
        $value = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        [pscustomobject]@{ Entries = @($value -split ';' | Where-Object { $_ }); Kind = $kind }
    }

    function Set-UserPath([string[]]$Entries, $Kind) {
        Set-ItemProperty -LiteralPath 'HKCU:\Environment' -Name 'Path' -Value ($Entries -join ';') -Type $Kind
        # Setting a user variable broadcasts WM_SETTINGCHANGE, so terminals started from now on see the new PATH
        [Environment]::SetEnvironmentVariable('LRW_INSTALL_REFRESH', '1', 'User')
        [Environment]::SetEnvironmentVariable('LRW_INSTALL_REFRESH', $null, 'User')
    }

    function Test-OurEntry([string]$Entry) {
        [Environment]::ExpandEnvironmentVariables($Entry).TrimEnd('\') -eq $installDir.TrimEnd('\')
    }

    function Test-McpPointsHere([string]$Claude) {
        (Invoke-Tool $Claude @('mcp', 'get', 'lrw')).Output -match [regex]::Escape($installDir)
    }

    function Show-Status {
        $current = Get-InstalledVersion
        Write-Host "lrw:    $(if ($current) { "$current ($exe)" } else { 'not installed' })"
        try {
            $latest = Get-LatestTag
            $verdict = if (-not $current) { 'run this script to install it' } elseif ("v$current" -eq $latest) { 'up to date' } else { 'update available - run this script again' }
            Write-Host "newest: $latest ($verdict)"
        }
        catch { Write-Host "newest: unknown ($($_.Exception.Message))" }
        $onPath = @((Get-UserPath).Entries | Where-Object { Test-OurEntry $_ }).Count -gt 0
        Write-Host "PATH:   $(if ($onPath) { "yes ($installDir)" } else { 'no' })"
        $claude = Get-Claude
        $mcp = if ($claude) { if (Test-McpPointsHere $claude) { 'registered' } else { 'not registered' } }
               elseif (Test-Path -LiteralPath $claudeDir) { 'unknown (the claude command is not on PATH)' }
               else { 'Claude Code is not installed' }
        Write-Host "MCP:    $mcp"
        $skill = if (Test-Path -LiteralPath (Join-Path $skillDir $skillMark)) { "installed ($skillDir)" }
                 elseif (Test-Path -LiteralPath $skillDir) { "another copy, left alone ($skillDir)" }
                 else { 'not installed' }
        Write-Host "skill:  $skill"
    }

    # Returns the release tag that is installed now
    function Install-Lrw {
        $arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        if ($arch -notin 'x64', 'arm64') { throw "Unsupported CPU '$arch': lrw is built for x64 and arm64." }
        $tag = if ($Version) { $Version } else { Get-LatestTag }
        if (-not $tag.StartsWith('v')) { $tag = "v$tag" }

        $current = Get-InstalledVersion
        if ($current -and "v$current" -eq $tag -and -not $Force) {
            Write-Host "lrw $current is already installed - nothing to download."
            return $tag
        }

        $asset = "lrw-win-$arch.zip"
        $work = Join-Path ([IO.Path]::GetTempPath()) "lrw-install-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $work | Out-Null
        try {
            Write-Host "Downloading lrw $tag for win-$arch..."
            $archive = Save-ReleaseFile $tag $asset $work
            Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $work 'lrw')
            if (-not (Test-Path -LiteralPath (Join-Path $work 'lrw\lrw.exe'))) { throw "$asset does not contain lrw.exe." }

            New-Item -ItemType Directory -Force -Path $installDir | Out-Null
            Get-ChildItem -LiteralPath $installDir -Filter 'lrw.exe.*.old' -File | Remove-Item -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $exe) {
                # A running lrw (lrw update itself, an MCP server of an open Claude session) locks lrw.exe; a locked file
                # can still be renamed, and the next lrw that starts deletes the renamed copy once nothing runs it
                try { Remove-Item -LiteralPath $exe -Force }
                catch { Move-Item -LiteralPath $exe -Destination "$exe.$(Get-Date -Format 'yyyyMMddHHmmss').old" }
            }
            Copy-Item -Path (Join-Path $work 'lrw\*') -Destination $installDir -Recurse -Force
        }
        finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

        $current = Get-InstalledVersion
        if (-not $current) { throw "The installed $exe does not start." }
        Write-Host "lrw $current installed in $installDir."
        return $tag
    }

    function Add-ToPath {
        $path = Get-UserPath
        if (-not ($path.Entries | Where-Object { Test-OurEntry $_ })) {
            Set-UserPath (@($path.Entries) + $installDir) $path.Kind
            Write-Host "Added $installDir to your user PATH."
        }
        # New terminals see the new PATH; this one gets it here, so lrw works right away
        if (@($env:Path -split ';') -notcontains $installDir) { $env:Path = "$env:Path;$installDir" }
    }

    function Register-Mcp {
        $manual = "claude mcp add --scope user lrw -- `"$exe`" mcp$(if ($McpProfile) { " --profile $McpProfile" })"
        $claude = Get-Claude
        if (-not $claude) {
            Write-Host "The claude command is not on PATH - register the MCP server with: $manual"
            return
        }
        # The program path never changes, so a registration made earlier (maybe with a profile) stays as it is
        if (-not $McpProfile -and (Test-McpPointsHere $claude)) {
            Write-Host "The MCP server 'lrw' is already registered in Claude Code."
            return
        }
        Invoke-Tool $claude @('mcp', 'remove', 'lrw', '--scope', 'user') | Out-Null
        $arguments = @('mcp', 'add', '--scope', 'user', 'lrw', '--', $exe, 'mcp')
        if ($McpProfile) { $arguments += @('--profile', $McpProfile) }
        $result = Invoke-Tool $claude $arguments
        if ($result.ExitCode -eq 0) { Write-Host "Registered the MCP server 'lrw' in Claude Code (user scope)." }
        else { Write-Warning "claude mcp add failed - run it by hand: $manual`n$($result.Output)" }
    }

    # The skill package of the release lrw came from, so its instructions match the program
    function Install-Skill([string]$Tag) {
        if ((Test-Path -LiteralPath $skillDir) -and -not (Test-Path -LiteralPath (Join-Path $skillDir $skillMark))) {
            Write-Host "A linux-remote-worker skill is already in $skillDir (not from this script) - left alone."
            return
        }
        $work = Join-Path ([IO.Path]::GetTempPath()) "lrw-skill-$([guid]::NewGuid().ToString('N'))"
        $new = "$skillDir.new-$([guid]::NewGuid().ToString('N'))"   # next to it, so the final move is a rename
        try {
            New-Item -ItemType Directory -Force -Path $work, $new | Out-Null
            try { $archive = Save-ReleaseFile $Tag 'lrw-skill.tar.gz' $work }
            catch { throw "lrw-skill.tar.gz of $Tag is not usable (older releases have none): $($_.Exception.Message)" }
            # tar.exe comes with Windows 10 1803 and newer
            $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
            if (-not (Test-Path -LiteralPath $tar)) { throw "$tar is missing - Windows 10 1803 or newer is needed" }
            $unpacked = Invoke-Tool $tar @('-xzf', $archive, '-C', $new)
            if ($unpacked.ExitCode -ne 0) { throw "could not unpack lrw-skill.tar.gz: $($unpacked.Output)" }
            if (-not (Select-String -LiteralPath (Join-Path $new 'SKILL.md') -Pattern '^name: linux-remote-worker' -Quiet)) {
                throw 'the downloaded SKILL.md is not the lrw skill'
            }
            Set-Content -LiteralPath (Join-Path $new $skillMark) -Value "Installed by scripts/install-cli.ps1 from $Tag; install-cli.ps1 -Uninstall removes it."
            if (Test-Path -LiteralPath $skillDir) { Remove-Item -LiteralPath $skillDir -Recurse -Force }
            Move-Item -LiteralPath $new -Destination $skillDir
            Write-Host "Added the Claude Code skill: $skillDir."
        }
        catch { Write-Warning "The skill was not added: $($_.Exception.Message)" }
        finally { Remove-Item -LiteralPath $new, $work -Recurse -Force -ErrorAction SilentlyContinue }
    }

    function Uninstall-Lrw {
        # Nothing is changed while lrw.exe is locked: it could not be deleted at the end
        $running = @(Get-Process -Name lrw -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) })
        if ($running.Count -gt 0) {
            throw "lrw is running ($($running.Count) process(es), usually MCP servers of open Claude Code sessions). Close Claude Code, then run this again."
        }

        $claude = Get-Claude
        if ($claude -and (Test-McpPointsHere $claude)) {
            Invoke-Tool $claude @('mcp', 'remove', 'lrw', '--scope', 'user') | Out-Null
            Write-Host "Removed the MCP server 'lrw' from Claude Code."
        }
        elseif (-not $claude -and (Test-Path -LiteralPath $claudeDir)) {
            Write-Host 'The claude command is not on PATH - if Claude Code still lists the MCP server lrw, remove it with: claude mcp remove lrw --scope user'
        }
        if (Test-Path -LiteralPath (Join-Path $skillDir $skillMark)) {
            Remove-Item -LiteralPath $skillDir -Recurse -Force
            Write-Host "Removed the Claude Code skill $skillDir."
        }
        $path = Get-UserPath
        $kept = @($path.Entries | Where-Object { -not (Test-OurEntry $_) })
        if ($kept.Count -ne $path.Entries.Count) {
            Set-UserPath $kept $path.Kind
            Write-Host "Removed $installDir from your user PATH."
        }
        if (Test-Path -LiteralPath $installDir) {
            Remove-Item -LiteralPath $installDir -Recurse -Force
            Write-Host "Removed $installDir."
        }
        # State of the setup script earlier versions used
        $legacy = Join-Path $env:LOCALAPPDATA 'lrw'
        if (Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Recurse -Force }
        Write-Host 'lrw is uninstalled. Saved servers and logs are kept.'
    }

    if ($Status) { Show-Status; return }
    if ($Uninstall) { Uninstall-Lrw; return }

    $tag = Install-Lrw
    if (-not $NoPath) { Add-ToPath }
    if ((Get-Claude) -or (Test-Path -LiteralPath $claudeDir)) {
        if (-not $NoMcp) { Register-Mcp }
        if (-not $NoSkill) { Install-Skill $tag }
    }
    elseif (-not ($NoMcp -and $NoSkill)) {
        Write-Host 'Claude Code is not installed - after installing it, run this again to connect lrw (MCP server and skill).'
    }
    Write-Host ''
    Write-Host 'Next:'
    Write-Host '  lrw profiles add <name> --host <server ip> --key <private key path> --make-default'
    Write-Host '  lrw connection test'
}
