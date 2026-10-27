<#
.SYNOPSIS
    Creates the key pair that signs desktop-app updates. Run it once, on your own computer (PowerShell 7).

.DESCRIPTION
    - The PUBLIC key goes to LinuxRemoteWorker/Update/update-signing.pub.pem: commit it. Every app built from then
      on accepts only updates signed with the matching private key.
    - The PRIVATE key is copied to the clipboard and nowhere else: add it to GitHub as the repository secret
      UPDATE_SIGNING_KEY (Settings → Secrets and variables → Actions → New repository secret), and keep a copy in
      your password manager. Never commit it.

    Replacing the key later (-Force) means apps installed before cannot verify updates signed with the new key -
    they have to be reinstalled once from the release's Setup.exe.

.EXAMPLE
    pwsh -File tools/new-update-signing-key.ps1
#>
[CmdletBinding()]
param(
    # Replace an existing key pair (see the description first)
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this with PowerShell 7 (pwsh) - it needs .NET 7 or later.' }

$repo = Split-Path -Parent $PSScriptRoot
$publicPath = Join-Path $repo 'LinuxRemoteWorker/Update/update-signing.pub.pem'
if ((Test-Path $publicPath) -and -not $Force) {
    throw "A signing key already exists: $publicPath. Replacing it breaks updates for installed apps (see Get-Help $PSCommandPath -Full); pass -Force only if you mean that."
}

$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $private = $key.ExportPkcs8PrivateKeyPem()
    $public = $key.ExportSubjectPublicKeyInfoPem()
} finally {
    $key.Dispose()
}

New-Item -ItemType Directory -Force (Split-Path $publicPath) | Out-Null
[System.IO.File]::WriteAllText($publicPath, ($public -replace "`r`n", "`n") + "`n")
Set-Clipboard -Value $private

Write-Host "Public key written: $publicPath" -ForegroundColor Green
Write-Host 'Private key copied to the clipboard - it is not stored anywhere else.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Next:'
Write-Host '  1. GitHub → the repository → Settings → Secrets and variables → Actions → New repository secret'
Write-Host '     Name: UPDATE_SIGNING_KEY    Secret: paste the clipboard'
Write-Host '  2. Save the private key in your password manager as well, then clear the clipboard.'
Write-Host '  3. Commit LinuxRemoteWorker/Update/update-signing.pub.pem.'
