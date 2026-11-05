<#
.SYNOPSIS
    Installs the SshPress module for the current user so `SshPress` / `sshp` work in every PowerShell session.
.DESCRIPTION
    Copies the SshPress folder into your personal module path. No admin rights, nothing else installed.
    From a clone of the repo:   .\install.ps1
    Both PowerShell editions:   .\install.ps1 -AllEditions
    Remove it again:            .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param([switch]$AllEditions, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$docs = [Environment]::GetFolderPath('MyDocuments')      # follows OneDrive folder redirection
$targets = @()
if ($AllEditions -or $PSVersionTable.PSEdition -ne 'Core') { $targets += Join-Path $docs 'WindowsPowerShell\Modules' }
if ($AllEditions -or $PSVersionTable.PSEdition -eq 'Core') { $targets += Join-Path $docs 'PowerShell\Modules' }

foreach ($dir in $targets) {
    $dest = Join-Path $dir 'SshPress'
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    if ($Uninstall) { Write-Host "Removed $dest"; continue }
    $src = Join-Path $PSScriptRoot 'SshPress'
    if (-not (Test-Path -LiteralPath (Join-Path $src 'SshPress.psd1'))) { throw "Run this from the repo root (SshPress\SshPress.psd1 not found)." }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force
    Get-ChildItem -LiteralPath $dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue   # zip downloads carry "mark of the web"
    Write-Host "Installed to $dest" -ForegroundColor Green
}
if ($Uninstall) { Remove-Module SshPress -ErrorAction SilentlyContinue; return }

if ((Get-ExecutionPolicy) -in 'Restricted', 'AllSigned') {
    Write-Warning "Your execution policy is '$(Get-ExecutionPolicy)' and blocks unsigned modules. Allow local ones with:  Set-ExecutionPolicy -Scope CurrentUser RemoteSigned"
}
Write-Host "Open a new PowerShell window and run:  sshp user@host     (help: Get-Help sshp -Full)" -ForegroundColor Cyan
