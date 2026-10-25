<#
.SYNOPSIS
Installs, updates, inspects and removes lrw, the command line of Linux Remote Worker.

.DESCRIPTION
Commands:
  install    Download the release build for this platform (default) or build it from source, put it
             in the install folder, make `lrw` callable from new terminals, and optionally register it
             as an MCP server in Claude Code.
  update     Install the newest version from the source the current installation came from.
  uninstall  Undo what install did: the program folder, the PATH entry or ~/.local/bin link, the MCP
             registration, the source cache. Saved servers and logs stay unless -Purge.
  status     What is installed, from where, whether `lrw` is on PATH, and whether an update exists.

Locations (no administrator rights needed):
  Windows        program  %LOCALAPPDATA%\Programs\lrw\lrw.exe   (folder added to the user PATH)
                 state    %LOCALAPPDATA%\lrw\                   (install.json, source cache)
  Linux / macOS  program  ~/.local/share/lrw/lrw                (linked as ~/.local/bin/lrw)
                 state    ~/.local/state/lrw/  and  ~/.cache/lrw/src

install.json records exactly what install changed, so update repeats it and uninstall reverts it.
Release downloads are checked against the release's SHA256SUMS.txt before anything is replaced.

.PARAMETER Command
install, update, uninstall or status.

.PARAMETER Source
release (download a published build, default) or build (compile from source; needs git and the .NET 8 SDK).

.PARAMETER Version
Release tag to install (for example v1.2.0), or latest.

.PARAMETER Repo
GitHub repository URL. With -Source build it may also be a local folder holding the sources.

.PARAMETER Ref
Branch or tag to build (-Source build from a URL). Default: the repository's default branch.

.PARAMETER InstallDir
Program folder. Default: see the locations above.

.PARAMETER SelfContained
-Source build only: bundle the .NET runtime (release builds always do).

.PARAMETER RegisterMcp
Register `lrw mcp` as an MCP server named lrw in Claude Code (needs the claude CLI).

.PARAMETER McpScope
user (all projects, default), project (.mcp.json of the current folder) or local.

.PARAMETER McpProfile
Saved lrw profile the MCP server connects to by default.

.PARAMETER NoPath
Do not touch PATH or ~/.local/bin.

.PARAMETER Force
install/update: reinstall even when the same version is installed.

.PARAMETER Purge
uninstall: also delete the command line's own data - cli.json and the cli-/mcp- logs. Saved servers
(profiles.json) are shared with the desktop app and are never deleted.

.PARAMETER Offline
status: do not ask GitHub for the newest version.

.PARAMETER Json
Print the result as JSON on stdout; progress goes to stderr.

.EXAMPLE
pwsh -File lrw-setup.ps1 install

.EXAMPLE
pwsh -File lrw-setup.ps1 install -Source build -Repo D:\Projects\linux-remote-worker -RegisterMcp

.EXAMPLE
pwsh -File lrw-setup.ps1 update

.EXAMPLE
pwsh -File lrw-setup.ps1 uninstall -Purge
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('install', 'update', 'uninstall', 'status')]
    [string]$Command,

    [ValidateSet('release', 'build')]
    [string]$Source = 'release',

    [string]$Version = 'latest',

    [string]$Repo = 'https://github.com/JustDimus/linux-remote-worker',

    [string]$Ref,

    [string]$InstallDir,

    [switch]$SelfContained,

    [switch]$RegisterMcp,

    [ValidateSet('user', 'project', 'local')]
    [string]$McpScope = 'user',

    [string]$McpProfile,

    [switch]$NoPath,

    [switch]$Force,

    [switch]$Purge,

    [switch]$Offline,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:OnWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or [bool](Get-Variable -Name IsWindows -ValueOnly -ErrorAction SilentlyContinue)
$script:OnMac = [bool](Get-Variable -Name IsMacOS -ValueOnly -ErrorAction SilentlyContinue)
$script:ExeName = if ($script:OnWindows) { 'lrw.exe' } else { 'lrw' }
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)

if ($PSVersionTable.PSEdition -eq 'Desktop') {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

function Join-Paths {
    param([string]$Base, [string[]]$Parts)

    $path = $Base
    foreach ($part in $Parts) { $path = Join-Path $path $part }
    return $path
}

function Get-StateDir {
    if ($script:OnWindows) { return Join-Path $env:LOCALAPPDATA 'lrw' }
    $base = if ($env:XDG_STATE_HOME) { $env:XDG_STATE_HOME } else { Join-Paths $HOME @('.local', 'state') }
    return Join-Path $base 'lrw'
}

function Get-CacheDir {
    if ($script:OnWindows) { return Join-Path (Get-StateDir) 'src' }
    $base = if ($env:XDG_CACHE_HOME) { $env:XDG_CACHE_HOME } else { Join-Path $HOME '.cache' }
    return Join-Paths $base @('lrw', 'src')
}

function Get-DefaultInstallDir {
    if ($script:OnWindows) { return Join-Paths $env:LOCALAPPDATA @('Programs', 'lrw') }
    return Join-Paths $HOME @('.local', 'share', 'lrw')
}

$script:StatePath = Join-Path (Get-StateDir) 'install.json'
$script:LinkDir = Join-Paths $HOME @('.local', 'bin')

function Write-Step {
    param([string]$Message)

    if ($Json) { [Console]::Error.WriteLine($Message) } else { Write-Host $Message }
}

function Get-FullPath {
    param([string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char]'\', [char]'/')
}

function Test-SamePath {
    param([string]$A, [string]$B)

    $comparison = if ($script:OnWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return [string]::Equals((Get-FullPath $A), (Get-FullPath $B), $comparison)
}

function Get-OptionalValue {
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments, [switch]$Stream)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Stream) {
            & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Step "    $_" }
            return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = '' }
        }
        $output = & $FilePath @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = (($output | Out-String).Trim()) }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Get-Rid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($arch -notin 'x64', 'arm64') { throw "Unsupported CPU architecture '$arch': builds exist for x64 and arm64." }
    $os = if ($script:OnWindows) { 'win' } elseif ($script:OnMac) { 'osx' } else { 'linux' }
    return "$os-$arch"
}

function Get-GitHubSlug {
    param([string]$Url)

    if ($Url -match '^https?://github\.com/([^/]+)/([^/]+?)(\.git)?/?$') { return "$($Matches[1])/$($Matches[2])" }
    throw "Release downloads need a GitHub repository URL (https://github.com/<owner>/<repo>), got '$Url'."
}

function Read-State {
    if (-not (Test-Path -LiteralPath $script:StatePath)) { return $null }
    return [System.IO.File]::ReadAllText($script:StatePath) | ConvertFrom-Json
}

function Save-State {
    param($State)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $script:StatePath) | Out-Null
    [System.IO.File]::WriteAllText($script:StatePath, (ConvertTo-Json -InputObject $State -Depth 5), $script:Utf8)
}

function Get-InstalledVersion {
    param([string]$Exe)

    if (-not (Test-Path -LiteralPath $Exe)) { return $null }
    $result = Invoke-Native $Exe @('version', '--text')
    if ($result.ExitCode -ne 0) { throw "The installed lrw does not start (exit $($result.ExitCode)): $($result.Output)" }
    return $result.Output.Trim()
}

# ── Release ────────────────────────────────────────────────────────────

function Get-LatestTag {
    param([string]$Slug)

    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Slug/releases/latest" -UseBasicParsing `
            -Headers @{ 'User-Agent' = 'lrw-setup'; 'Accept' = 'application/vnd.github+json' }
        return [string]$release.tag_name
    }
    catch {
        $failure = $_
        $status = 0
        try { $status = [int]$failure.Exception.Response.StatusCode } catch { $status = 0 }
        if ($status -eq 404) { throw "$Slug has no published release yet. Build from source instead: -Source build" }
        throw "Could not ask GitHub for the newest release of ${Slug}: $($failure.Exception.Message)"
    }
}

function Save-Url {
    param([string]$Uri, [string]$Path)

    $progress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $Uri -OutFile $Path -UseBasicParsing -Headers @{ 'User-Agent' = 'lrw-setup' }
    }
    catch {
        throw "Download failed: $Uri ($($_.Exception.Message))"
    }
    finally {
        $ProgressPreference = $progress
    }
}

function Get-ReleasePayload {
    param([string]$Slug, [string]$Tag, [string]$Rid, [string]$Work)

    $asset = if ($script:OnWindows) { "lrw-$Rid.zip" } else { "lrw-$Rid.tar.gz" }
    $base = "https://github.com/$Slug/releases/download/$Tag"
    $archive = Join-Path $Work $asset
    $sums = Join-Path $Work 'SHA256SUMS.txt'

    Write-Step "Downloading $asset ($Tag)..."
    Save-Url "$base/$asset" $archive
    Save-Url "$base/SHA256SUMS.txt" $sums

    $expected = $null
    foreach ($line in [System.IO.File]::ReadAllLines($sums)) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$' -and $Matches[2].Trim() -eq $asset) { $expected = $Matches[1] }
    }
    if ($null -eq $expected) { throw "SHA256SUMS.txt of $Tag has no entry for $asset." }
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actual -ne $expected.ToUpperInvariant()) { throw "Checksum mismatch for $asset - the download is corrupt or was tampered with. Nothing was installed." }
    Write-Step "Checksum OK."

    $extract = Join-Path $Work 'payload'
    New-Item -ItemType Directory -Path $extract | Out-Null
    if ($asset.EndsWith('.zip')) {
        Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
    }
    else {
        $tar = Invoke-Native 'tar' @('-xzf', $archive, '-C', $extract)
        if ($tar.ExitCode -ne 0) { throw "Could not unpack ${asset}: $($tar.Output)" }
    }
    return $extract
}

# ── Build from source ──────────────────────────────────────────────────

function Get-SourceTree {
    param([string]$Location, [string]$Branch)

    if (Test-Path -LiteralPath $Location -PathType Container) {
        return [pscustomobject]@{ Path = (Get-FullPath $Location); CacheDir = $null; Repo = (Get-FullPath $Location); Ref = $null }
    }

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Building from a repository URL needs git on PATH.' }
    $cache = Get-CacheDir
    if (Test-Path -LiteralPath $cache) { Remove-Item -LiteralPath $cache -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cache) | Out-Null

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.AddRange([string[]]@('clone', '--depth', '1'))
    if ($Branch) { $arguments.AddRange([string[]]@('--branch', $Branch)) }
    $arguments.AddRange([string[]]@('--', $Location, $cache))
    Write-Step "Cloning $Location$(if ($Branch) { " ($Branch)" })..."
    $clone = Invoke-Native 'git' $arguments.ToArray()
    if ($clone.ExitCode -ne 0) { throw "git clone failed: $($clone.Output)" }
    return [pscustomobject]@{ Path = $cache; CacheDir = $cache; Repo = $Location; Ref = $Branch }
}

function Get-Commit {
    param([string]$Path)

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
    $result = Invoke-Native 'git' @('-C', $Path, 'rev-parse', 'HEAD')
    if ($result.ExitCode -ne 0) { return $null }
    return $result.Output.Trim()
}

function Get-RemoteCommit {
    param([string]$Url, [string]$Branch)

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
    $refName = if ($Branch) { $Branch } else { 'HEAD' }
    $result = Invoke-Native 'git' @('ls-remote', $Url, $refName)
    if ($result.ExitCode -ne 0 -or -not $result.Output) { return $null }
    return ($result.Output -split '\s+')[0]
}

function Invoke-Build {
    param($Tree, [string]$Rid, [bool]$Bundled, [string]$Work)

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Building needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0' }
    $sdks = @((Invoke-Native 'dotnet' @('--list-sdks')).Output -split "`n" | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 })
    if ($sdks.Count -eq 0) { throw 'Building needs the .NET 8 SDK or newer (dotnet --list-sdks shows none).' }

    $project = Join-Paths $Tree.Path @('LinuxRemoteWorker.Cli', 'LinuxRemoteWorker.Cli.csproj')
    if (-not (Test-Path -LiteralPath $project)) { throw "No LinuxRemoteWorker.Cli project in $($Tree.Path)." }

    $output = Join-Path $Work 'payload'
    Write-Step "Building lrw for $Rid (this takes a minute)..."
    $build = Invoke-Native 'dotnet' @('publish', $project, '-c', 'Release', '-r', $Rid,
        '--self-contained', $Bundled.ToString().ToLowerInvariant(), '-p:PublishSingleFile=true', '-p:DebugType=none',
        '--nologo', '-o', $output) -Stream
    if ($build.ExitCode -ne 0) { throw "dotnet publish failed (exit $($build.ExitCode)) - see the output above." }
    return $output
}

# ── Placing the program ────────────────────────────────────────────────

function Install-Payload {
    param([string]$From, [string]$To)

    New-Item -ItemType Directory -Force -Path $To | Out-Null
    $exe = Join-Path $To $script:ExeName

    if ($script:OnWindows) {
        Get-ChildItem -LiteralPath $To -Filter '*.old' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $exe) {
            # A running lrw (an MCP server of an open Claude session) locks lrw.exe; a locked exe can still be renamed
            $old = "$exe.$(Get-Date -Format 'yyyyMMddHHmmss').old"
            Move-Item -LiteralPath $exe -Destination $old
        }
    }

    foreach ($entry in Get-ChildItem -LiteralPath $From -Force) {
        if ($entry.Extension -eq '.pdb') { continue }
        Copy-Item -LiteralPath $entry.FullName -Destination $To -Recurse -Force
    }
    if (-not $script:OnWindows) {
        $chmod = Invoke-Native 'chmod' @('+x', $exe)
        if ($chmod.ExitCode -ne 0) { throw "chmod +x $exe failed: $($chmod.Output)" }
    }
    return $exe
}

function Get-UserPath {
    $key = Get-Item -LiteralPath 'HKCU:\Environment'
    $kind = if ($key.GetValueNames() -contains 'Path') { $key.GetValueKind('Path') } else { [Microsoft.Win32.RegistryValueKind]::ExpandString }
    $value = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    return [pscustomobject]@{ Entries = @($value -split ';' | Where-Object { $_ }); Kind = $kind }
}

function Set-UserPath {
    param([string[]]$Entries, $Kind)

    Set-ItemProperty -LiteralPath 'HKCU:\Environment' -Name 'Path' -Value ($Entries -join ';') -Type $Kind
    # SetEnvironmentVariable broadcasts WM_SETTINGCHANGE, so terminals started from now on see the new PATH
    [Environment]::SetEnvironmentVariable('LRW_SETUP_REFRESH', '1', 'User')
    [Environment]::SetEnvironmentVariable('LRW_SETUP_REFRESH', $null, 'User')
}

function Test-PathEntry {
    param([string]$Entry, [string]$Dir)

    try { return Test-SamePath ([Environment]::ExpandEnvironmentVariables($Entry)) $Dir } catch { return $false }
}

# Returns $true when this run added the folder (so uninstall removes it), $false when it was already there
function Add-ToPath {
    param([string]$Dir)

    $path = Get-UserPath
    if ($path.Entries | Where-Object { Test-PathEntry $_ $Dir }) { return $false }
    Set-UserPath (@($path.Entries) + $Dir) $path.Kind
    $env:Path = "$env:Path;$Dir"
    return $true
}

function Remove-FromPath {
    param([string]$Dir)

    $path = Get-UserPath
    $kept = @($path.Entries | Where-Object { -not (Test-PathEntry $_ $Dir) })
    if ($kept.Count -ne $path.Entries.Count) { Set-UserPath $kept $path.Kind }
}

# Returns the link when it points at this installation (created now or earlier), else $null
function Add-Link {
    param([string]$Exe)

    New-Item -ItemType Directory -Force -Path $script:LinkDir | Out-Null
    $link = Join-Path $script:LinkDir 'lrw'
    $existing = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        $target = Get-OptionalValue $existing 'Target'
        if ($null -ne $target -and (Test-SamePath ([string]@($target)[0]) $Exe)) { return $link }
        Write-Step "warning: $link already exists and is not this installation's link - left alone."
        return $null
    }
    New-Item -ItemType SymbolicLink -Path $link -Target $Exe | Out-Null
    $onPath = @($env:PATH -split ':') | Where-Object { $_ -and (Test-SamePath $_ $script:LinkDir) }
    if (-not $onPath) {
        Write-Step "note: $($script:LinkDir) is not on PATH. Add this line to ~/.profile (bash) or ~/.zshrc (zsh):"
        Write-Step '  export PATH="$HOME/.local/bin:$PATH"'
    }
    return $link
}

function Remove-Link {
    param([string]$Link, [string]$Exe)

    $existing = Get-Item -LiteralPath $Link -Force -ErrorAction SilentlyContinue
    if ($null -eq $existing) { return }
    $target = Get-OptionalValue $existing 'Target'
    if ($null -ne $target -and (Test-SamePath ([string]@($target)[0]) $Exe)) { Remove-Item -LiteralPath $Link -Force }
}

function Get-Claude {
    $command = Get-Command claude -ErrorAction SilentlyContinue
    if ($null -eq $command) { return $null }
    return $command.Source
}

function Register-Mcp {
    param([string]$Exe, [string]$Scope, [string]$ProfileName)

    $claude = Get-Claude
    $manual = "claude mcp add --scope $Scope lrw -- `"$Exe`" mcp" + $(if ($ProfileName) { " --profile $ProfileName" } else { '' })
    if ($null -eq $claude) {
        Write-Step "warning: the claude CLI is not on PATH - register the MCP server by hand: $manual"
        return $null
    }

    Invoke-Native $claude @('mcp', 'remove', 'lrw', '--scope', $Scope) | Out-Null
    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.AddRange([string[]]@('mcp', 'add', '--scope', $Scope, 'lrw', '--', $Exe, 'mcp'))
    if ($ProfileName) { $arguments.AddRange([string[]]@('--profile', $ProfileName)) }
    $result = Invoke-Native $claude $arguments.ToArray()
    if ($result.ExitCode -ne 0) { throw "claude mcp add failed: $($result.Output)" }
    Write-Step "Registered the MCP server 'lrw' ($Scope scope)."
    return [pscustomobject][ordered]@{ name = 'lrw'; scope = $Scope; profile = $ProfileName }
}

function Unregister-Mcp {
    param($Mcp)

    $claude = Get-Claude
    if ($null -eq $claude -or $null -eq $Mcp) { return }
    $result = Invoke-Native $claude @('mcp', 'remove', [string]$Mcp.name, '--scope', [string]$Mcp.scope)
    if ($result.ExitCode -eq 0) { Write-Step "Removed the MCP server '$($Mcp.name)'." }
}

# ── Commands ───────────────────────────────────────────────────────────

function Invoke-Install {
    param($Previous, [bool]$IsUpdate)

    # An update repeats the recorded installation; options passed explicitly override it
    $bound = $script:Bound
    $source = if ($bound.ContainsKey('Source') -or $null -eq $Previous) { $Source } else { [string]$Previous.source }
    $repo = if ($bound.ContainsKey('Repo') -or $null -eq $Previous) { $Repo } else { [string]$Previous.repo }
    $branch = if ($bound.ContainsKey('Ref') -or $null -eq $Previous) { $Ref } else { [string](Get-OptionalValue $Previous 'ref') }
    $dir = if ($bound.ContainsKey('InstallDir')) { Get-FullPath $InstallDir } elseif ($null -ne $Previous) { [string]$Previous.installDir } else { Get-DefaultInstallDir }
    $bundled = if ($bound.ContainsKey('SelfContained') -or $null -eq $Previous) { [bool]$SelfContained } else { [bool](Get-OptionalValue $Previous 'selfContained') }
    $rid = Get-Rid
    $exe = Join-Path $dir $script:ExeName

    if ($null -ne $Previous -and -not $IsUpdate -and -not $Force -and (Test-Path -LiteralPath $exe)) {
        Write-Step "lrw $($Previous.version) is already installed in $dir - use update, or install -Force to reinstall."
        return [pscustomobject][ordered]@{ ok = $true; action = 'none'; version = $Previous.version; installDir = $dir }
    }

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ('lrw-setup-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $tag = $null
        $commit = $null
        $cacheDir = $null
        if ($source -eq 'release') {
            $slug = Get-GitHubSlug $repo
            $tag = if ($Version -eq 'latest') { Get-LatestTag $slug } else { $Version }
            if ($null -ne $Previous -and [string](Get-OptionalValue $Previous 'tag') -eq $tag -and -not $Force -and (Test-Path -LiteralPath $exe)) {
                Write-Step "lrw $tag is already installed - nothing to do."
                return [pscustomobject][ordered]@{ ok = $true; action = 'up-to-date'; version = $Previous.version; tag = $tag; installDir = $dir }
            }
            $payload = Get-ReleasePayload $slug $tag $rid $work
            $bundled = $true
        }
        else {
            $isFolder = Test-Path -LiteralPath $repo -PathType Container
            if ($IsUpdate -and -not $Force -and $null -ne $Previous) {
                $current = if ($isFolder) { Get-Commit $repo } else { Get-RemoteCommit $repo $branch }
                if ($null -ne $current -and $current -eq [string](Get-OptionalValue $Previous 'commit') -and (Test-Path -LiteralPath $exe)) {
                    Write-Step "lrw is already built from the newest commit ($($current.Substring(0, 7))) - nothing to do. Use -Force to rebuild."
                    return [pscustomobject][ordered]@{ ok = $true; action = 'up-to-date'; version = $Previous.version; commit = $current; installDir = $dir }
                }
            }
            $tree = Get-SourceTree $repo $branch
            $commit = Get-Commit $tree.Path
            $cacheDir = $tree.CacheDir
            $repo = $tree.Repo
            $payload = Invoke-Build $tree $rid $bundled $work
        }

        Write-Step "Installing into $dir..."
        $exe = Install-Payload $payload $dir
        $installedVersion = Get-InstalledVersion $exe

        $pathAdded = [bool](Get-OptionalValue $Previous 'pathAdded')
        $linkPath = [string](Get-OptionalValue $Previous 'linkPath')
        if (-not $NoPath) {
            if ($script:OnWindows) {
                if (Add-ToPath $dir) { $pathAdded = $true; Write-Step "Added $dir to your user PATH - new terminals will find 'lrw'." }
            }
            else {
                $link = Add-Link $exe
                if ($null -ne $link) { $linkPath = $link }
            }
        }

        $mcp = Get-OptionalValue $Previous 'mcp'
        if ($RegisterMcp) { $mcp = Register-Mcp $exe $McpScope $McpProfile }

        $state = [pscustomobject][ordered]@{
            version       = $installedVersion
            tag           = $tag
            source        = $source
            repo          = $repo
            ref           = $branch
            commit        = $commit
            rid           = $rid
            selfContained = $bundled
            installDir    = $dir
            exe           = $exe
            cacheDir      = $cacheDir
            pathAdded     = $pathAdded
            linkPath      = if ($linkPath) { $linkPath } else { $null }
            mcp           = $mcp
            installedAt   = if ($null -ne $Previous) { [string]$Previous.installedAt } else { (Get-Date).ToUniversalTime().ToString('o') }
            updatedAt     = (Get-Date).ToUniversalTime().ToString('o')
        }
        Save-State $state

        $verb = if ($null -eq $Previous) { 'installed' } elseif ($IsUpdate) { 'updated' } else { 'reinstalled' }
        $from = if ($source -eq 'release') { "release $tag" } else { "source $repo$(if ($commit) { " @ $($commit.Substring(0, 7))" })" }
        Write-Step "lrw $installedVersion $verb from $from."
        Write-Step "Next: lrw profiles add <name> --host <server ip> --key <private key path>; lrw connection test"
        return [pscustomobject][ordered]@{ ok = $true; action = $verb; version = $installedVersion; tag = $tag; commit = $commit; exe = $exe; pathAdded = $pathAdded; linkPath = $state.linkPath; mcp = $mcp }
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Uninstall {
    $state = Read-State
    $dir = if ($script:Bound.ContainsKey('InstallDir')) { Get-FullPath $InstallDir } elseif ($null -ne $state) { [string]$state.installDir } else { Get-DefaultInstallDir }
    $exe = Join-Path $dir $script:ExeName

    if ($null -eq $state -and -not (Test-Path -LiteralPath $dir)) {
        Write-Step 'lrw is not installed - nothing to remove.'
        if ($Purge) { Remove-UserData }
        return [pscustomobject][ordered]@{ ok = $true; action = 'none' }
    }

    if ($null -ne $state) {
        Unregister-Mcp (Get-OptionalValue $state 'mcp')
        if ($script:OnWindows -and [bool](Get-OptionalValue $state 'pathAdded')) {
            Remove-FromPath $dir
            Write-Step "Removed $dir from your user PATH."
        }
        $link = [string](Get-OptionalValue $state 'linkPath')
        if ($link) { Remove-Link $link $exe }
    }

    if (Test-Path -LiteralPath $dir) {
        try {
            Remove-Item -LiteralPath $dir -Recurse -Force
        }
        catch {
            throw "Could not delete $dir - lrw is probably running (an MCP server of an open Claude session). Close it and run uninstall again. ($($_.Exception.Message))"
        }
        Write-Step "Deleted $dir."
    }

    $stateDir = Get-StateDir
    if (Test-Path -LiteralPath $stateDir) { Remove-Item -LiteralPath $stateDir -Recurse -Force }
    $cache = Get-CacheDir
    if (Test-Path -LiteralPath $cache) { Remove-Item -LiteralPath $cache -Recurse -Force }
    if ($Purge) { Remove-UserData }

    Write-Step 'lrw is uninstalled.'
    return [pscustomobject][ordered]@{ ok = $true; action = 'uninstalled'; installDir = $dir; purged = [bool]$Purge }
}

# The command line's own files only; profiles.json and app-*.log belong to the desktop app as well
function Remove-UserData {
    $data = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'LinuxRemoteWorker'
    $removed = 0
    $cli = Join-Path $data 'cli.json'
    if (Test-Path -LiteralPath $cli) { Remove-Item -LiteralPath $cli -Force; $removed++ }
    $logs = Join-Path $data 'logs'
    if (Test-Path -LiteralPath $logs) {
        foreach ($file in Get-ChildItem -LiteralPath $logs -File | Where-Object { $_.Name -like 'cli-*.log' -or $_.Name -like 'mcp-*.log' }) {
            Remove-Item -LiteralPath $file.FullName -Force
            $removed++
        }
    }
    Write-Step "Purged $removed file(s) of the command line's own data (saved servers kept)."
}

function Invoke-Status {
    $state = Read-State
    $onPath = Get-Command lrw -ErrorAction SilentlyContinue
    $result = [ordered]@{
        installed       = $false
        version         = $null
        source          = $null
        tag             = $null
        commit          = $null
        exe             = $null
        onPath          = if ($null -ne $onPath) { $onPath.Source } else { $null }
        mcp             = $null
        latest          = $null
        updateAvailable = $null
        rid             = Get-Rid
    }

    if ($null -ne $state) {
        $result.installed = Test-Path -LiteralPath ([string]$state.exe)
        $result.version = $state.version
        $result.source = $state.source
        $result.tag = Get-OptionalValue $state 'tag'
        $result.commit = Get-OptionalValue $state 'commit'
        $result.exe = $state.exe
        $result.mcp = Get-OptionalValue $state 'mcp'

        if (-not $Offline) {
            try {
                if ($state.source -eq 'release') {
                    $result.latest = Get-LatestTag (Get-GitHubSlug ([string]$state.repo))
                    $result.updateAvailable = $result.latest -ne $result.tag
                }
                elseif (-not (Test-Path -LiteralPath ([string]$state.repo) -PathType Container)) {
                    $result.latest = Get-RemoteCommit ([string]$state.repo) ([string](Get-OptionalValue $state 'ref'))
                    if ($null -ne $result.latest) { $result.updateAvailable = $result.latest -ne $result.commit }
                }
            }
            catch {
                Write-Step "note: could not check for updates - $($_.Exception.Message)"
            }
        }
    }

    if (-not $Json) {
        if (-not $result.installed) {
            Write-Host ('lrw is not installed by this script.' + $(if ($result.onPath) { " Another lrw is on PATH: $($result.onPath)" } else { '' }))
        }
        else {
            $from = if ($result.source -eq 'release') { "release $($result.tag)" } else { "source $($state.repo)$(if ($result.commit) { " @ $(([string]$result.commit).Substring(0, 7))" })" }
            Write-Host "lrw $($result.version) from $from"
            Write-Host "  program: $($result.exe)"
            Write-Host "  on PATH: $(if ($result.onPath) { $result.onPath } else { 'no - open a new terminal, or see install notes' })"
            Write-Host "  MCP:     $(if ($result.mcp) { "registered as '$($result.mcp.name)' ($($result.mcp.scope) scope)" } else { 'not registered by this script' })"
            if ($null -ne $result.updateAvailable) {
                Write-Host "  update:  $(if ($result.updateAvailable) { "available ($($result.latest)) - run: lrw-setup.ps1 update" } else { 'up to date' })"
            }
        }
    }
    return [pscustomobject]$result
}

$script:Bound = $PSBoundParameters
try {
    $result = switch ($Command) {
        'install' { Invoke-Install (Read-State) $false }
        'update' {
            $previous = Read-State
            if ($null -eq $previous) { throw 'lrw is not installed by this script - run install first.' }
            Invoke-Install $previous $true
        }
        'uninstall' { Invoke-Uninstall }
        'status' { Invoke-Status }
    }
    if ($Json) { ConvertTo-Json -InputObject $result -Depth 5 }
    exit 0
}
catch {
    if ($Json) {
        ConvertTo-Json -InputObject ([ordered]@{ ok = $false; error = $_.Exception.Message })
    }
    else {
        Write-Host "error: $($_.Exception.Message)"
    }
    exit 1
}
