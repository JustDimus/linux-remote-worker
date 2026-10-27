<#
.SYNOPSIS
Installs or updates lrw, the command line of Linux Remote Worker, on Windows. When Claude Code is installed it also
registers lrw as the MCP server "lrw" and adds the linux-remote-worker skill.

.DESCRIPTION
One line in PowerShell - no clone, no administrator rights, no .NET:

    irm https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/scripts/install-cli.ps1 | iex

With options:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/scripts/install-cli.ps1))) -McpProfile prod

Run it again to update - or, after installing Claude Code, to connect lrw to it. The program is handled by the
skill's lrw-setup.ps1 - from this checkout, or downloaded: the newest release build for this CPU, checked against
SHA256SUMS.txt, goes to %LOCALAPPDATA%\Programs\lrw and onto the user PATH, and `claude mcp add` registers the MCP
server; lrw-setup.ps1 status, update and uninstall work on the result. The skill comes from the same release and
goes to %USERPROFILE%\.claude\skills\linux-remote-worker; a skill put there some other way (a skills pack) is left
alone. Windows PowerShell 5.1 or PowerShell 7.
#>
param(
    # Saved lrw profile the MCP server connects to by default
    [string]$McpProfile,
    # Do not register the MCP server
    [switch]$NoMcp,
    # Do not add the skill
    [switch]$NoSkill,
    # Leave PATH alone
    [switch]$NoPath,
    # A release such as v0.1.0 instead of the newest
    [string]$Version = 'latest',
    # Remove lrw, its PATH entry, its MCP registration and the skill (saved servers and logs stay)
    [switch]$Uninstall
)

# A child scope: run through iex, nothing here leaks into the caller's session, and errors never close it
& {
    $ErrorActionPreference = 'Stop'
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    $setup = if ($PSScriptRoot) { Join-Path $PSScriptRoot '..\.claude\skills\linux-remote-worker\scripts\lrw-setup.ps1' }
    $download = $null
    if (-not $setup -or -not (Test-Path -LiteralPath $setup)) {
        $download = Join-Path ([IO.Path]::GetTempPath()) "lrw-setup-$([guid]::NewGuid().ToString('N')).ps1"
        Invoke-WebRequest -UseBasicParsing -OutFile $download `
            -Uri 'https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/.claude/skills/linux-remote-worker/scripts/lrw-setup.ps1'
        $setup = $download
    }

    $claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
    # The claude command registers the MCP server; Claude Code used only from an editor may not have it on PATH
    $claudeCli = [bool](Get-Command claude -ErrorAction SilentlyContinue)
    $claudeInstalled = $claudeCli -or (Test-Path -LiteralPath $claudeDir)

    $state = Join-Path $env:LOCALAPPDATA 'lrw\install.json'
    $command = if ($Uninstall) { 'uninstall' } elseif (Test-Path -LiteralPath $state) { 'update' } else { 'install' }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $setup, $command)
    if (-not $Uninstall) {
        $arguments += @('-Version', $Version)
        if ($NoPath) { $arguments += '-NoPath' }
        if (-not $NoMcp -and $claudeCli) {
            $arguments += '-RegisterMcp'
            if ($McpProfile) { $arguments += @('-McpProfile', $McpProfile) }
        }
    }

    # In its own process: lrw-setup.ps1 ends with exit, and the process gets its own execution policy
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    $shell = if ($pwsh) { $pwsh.Source } else { Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
    try {
        & $shell @arguments
        if ($LASTEXITCODE -ne 0) { throw "lrw-setup.ps1 $command failed (exit $LASTEXITCODE) - see above." }
    }
    finally {
        if ($download) { Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue }
    }

    $skillDir = Join-Path $claudeDir 'skills\linux-remote-worker'
    $mark = '.installed-by-lrw-install-cli'
    if ($Uninstall) {
        if (Test-Path -LiteralPath (Join-Path $skillDir $mark)) {
            Remove-Item -LiteralPath $skillDir -Recurse -Force
            Write-Host "Removed the Claude Code skill $skillDir."
        }
        return
    }

    # New terminals see the new PATH; this one gets it here, so lrw works right away
    $installed = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
    if (-not $NoPath -and $installed.installDir -and (@($env:Path -split ';') -notcontains $installed.installDir)) {
        $env:Path = "$env:Path;$($installed.installDir)"
    }

    if (-not $claudeInstalled) {
        if (-not ($NoMcp -and $NoSkill)) {
            Write-Host 'Claude Code is not installed - after installing it, run this again to connect lrw (MCP server and skill).'
        }
        return
    }
    if (-not $NoMcp -and -not $claudeCli) {
        Write-Host "The claude command is not on PATH here, so this run did not register the MCP server. If Claude Code" `
            "does not list lrw yet, run where claude works: claude mcp add --scope user lrw -- `"$($installed.exe)`" mcp"
    }

    # The skill's files from the release lrw came from, so its instructions match the program
    if ($NoSkill) { return }
    if ((Test-Path -LiteralPath $skillDir) -and -not (Test-Path -LiteralPath (Join-Path $skillDir $mark))) {
        Write-Host "A linux-remote-worker skill is already in $skillDir (not from this script) - left alone."
        return
    }
    $ref = if ($installed.tag) { $installed.tag } else { 'main' }
    $new = "$skillDir.new-$([guid]::NewGuid().ToString('N'))"   # next to it, so the final move is a rename
    try {
        foreach ($file in 'SKILL.md', 'reference.md', 'scripts/lrw-setup.ps1') {
            $target = Join-Path $new $file
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Invoke-WebRequest -UseBasicParsing -OutFile $target `
                -Uri "https://raw.githubusercontent.com/JustDimus/linux-remote-worker/$ref/.claude/skills/linux-remote-worker/$file"
        }
        if (-not (Select-String -LiteralPath (Join-Path $new 'SKILL.md') -Pattern '^name: linux-remote-worker' -Quiet)) {
            throw 'the downloaded SKILL.md is not the lrw skill'
        }
        Set-Content -LiteralPath (Join-Path $new $mark) -Value "Installed by scripts/install-cli.ps1 from $ref; install-cli.ps1 -Uninstall removes it."
        if (Test-Path -LiteralPath $skillDir) { Remove-Item -LiteralPath $skillDir -Recurse -Force }
        Move-Item -LiteralPath $new -Destination $skillDir
        Write-Host "Added the Claude Code skill: $skillDir."
    }
    catch { Write-Warning "The skill was not added: $($_.Exception.Message)" }
    finally { Remove-Item -LiteralPath $new -Recurse -Force -ErrorAction SilentlyContinue }
}
