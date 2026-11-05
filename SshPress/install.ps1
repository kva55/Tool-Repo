<#
.SYNOPSIS
    Installs the SshPress module for the current user so `SshPress` / `sshp` work in every PowerShell session.
.DESCRIPTION
    Run it from the folder that holds SshPress.psd1, SshPress.psm1 and lib\ (this script sits next to them).
    Copies the module into your personal module path. No admin rights, nothing else installed.
        .\install.ps1                 install for the current PowerShell edition
        .\install.ps1 -AllEditions    Windows PowerShell 5.1 and PowerShell 7
        .\install.ps1 -Uninstall      remove it again
#>
[CmdletBinding()]
param([switch]$AllEditions, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'

# Module source: this folder (flat layout), or a SshPress subfolder (repo-root layout).
$src = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $src 'SshPress.psd1'))) { $src = Join-Path $PSScriptRoot 'SshPress' }
if (-not $Uninstall -and -not (Test-Path -LiteralPath (Join-Path $src 'SshPress.psd1'))) {
    throw "SshPress.psd1 not found next to install.ps1 (or in a SshPress subfolder)."
}

$docs = [Environment]::GetFolderPath('MyDocuments')      # follows OneDrive folder redirection
$targets = @()
if ($AllEditions -or $PSVersionTable.PSEdition -ne 'Core') { $targets += Join-Path $docs 'WindowsPowerShell\Modules' }
if ($AllEditions -or $PSVersionTable.PSEdition -eq 'Core') { $targets += Join-Path $docs 'PowerShell\Modules' }

foreach ($dir in $targets) {
    $dest = Join-Path $dir 'SshPress'
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    if ($Uninstall) { Write-Host "Removed $dest"; continue }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    # Only the module itself, not install.ps1 / README / LICENSE.
    foreach ($item in 'SshPress.psd1', 'SshPress.psm1', 'lib') {
        Copy-Item -LiteralPath (Join-Path $src $item) -Destination $dest -Recurse -Force
    }
    Get-ChildItem -LiteralPath $dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue   # zip downloads carry "mark of the web"
    Write-Host "Installed to $dest" -ForegroundColor Green
}
if ($Uninstall) { Remove-Module SshPress -ErrorAction SilentlyContinue; return }

if ((Get-ExecutionPolicy) -in 'Restricted', 'AllSigned') {
    Write-Warning "Your execution policy is '$(Get-ExecutionPolicy)' and blocks unsigned modules. Allow local ones with:  Set-ExecutionPolicy -Scope CurrentUser RemoteSigned"
}
Write-Host "Open a new PowerShell window and run:  sshp user@host     (help: Get-Help sshp -Full)" -ForegroundColor Cyan
