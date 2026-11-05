#Requires -Version 5.1
# SshPress - show an SSH folder as a normal folder in Windows Explorer.
# Windows "cloud files" API (the one OneDrive uses) + the OpenSSH client that ships with Windows.

$script:CfType = $null

# Compile the C# provider once. The type name includes a hash of the source, so a newer module
# loaded into a session that already ran an older one never reuses stale code.
function Get-SpProvider {
    if ($script:CfType) { return $script:CfType }
    $src = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'lib/SshPress.Cf.cs') -Raw
    $sha = [Security.Cryptography.SHA256]::Create()
    $id = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($src))) -replace '-', '').Substring(0, 10)
    $name = "SshPress.Cf$id"
    if (-not ($name -as [type])) {
        try { Add-Type -TypeDefinition $src.Replace('public static class Cf', "public static class Cf$id") -Language CSharp }
        catch { throw "SshPress could not compile its provider: $_" }
    }
    $script:CfType = $name -as [type]
    return $script:CfType
}

# One-time-per-host setup, done in the console so every prompt is visible:
#   1. unknown host  -> ssh shows the fingerprint and YOU confirm it (never auto-trusted)
#   2. changed key   -> hard stop
#   3. no key login  -> offer to create a key and install the public key (asks first)
function Confirm-SpLogin {
    [CmdletBinding()]
    param([string]$Target, [int]$Port, [string]$Key)

    $common = @('-p', $Port, '-o', 'ConnectTimeout=10')
    if ($Key) { $common += @('-i', $Key) }
    $probe = $common + @('-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-n', '--', $Target, 'exit')

    $out = (& ssh @probe 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0) { return }

    if ($out -match 'REMOTE HOST IDENTIFICATION HAS CHANGED') {
        throw "The host key for $Target has CHANGED. This can mean someone is intercepting the connection. If the server was reinstalled, remove the old key with:  ssh-keygen -R <host>`n$out"
    }
    if ($out -match 'Host key verification failed') {
        Write-Host "First connection to $Target. Check the fingerprint below is the one you expect, then answer yes or no." -ForegroundColor Yellow
        $ask = $common + @('-o', 'StrictHostKeyChecking=ask', '--', $Target, 'exit')
        & ssh @ask
        $out = (& ssh @probe 2>&1 | Out-String)
        if ($LASTEXITCODE -eq 0) { return }
        if ($out -match 'Host key verification failed') { throw "Host key for $Target was not accepted." }
    }
    if ($out -notmatch 'Permission denied') { throw "Cannot log in to ${Target}:`n$out" }

    Write-Verbose "Key login to $Target failed: $($out.Trim())"
    $answer = Read-Host "No key login to $Target yet. Create an SSH key if needed and install its public key on the server? (y/N)"
    if ($answer -notmatch '^[Yy]') { throw 'Key login is required (SshPress never stores or sends passwords).' }

    $priv = if ($Key) { $Key } else { Join-Path $HOME '.ssh\id_ed25519' }
    if (-not (Test-Path -LiteralPath $priv)) {
        New-Item -ItemType Directory -Force -Path (Split-Path $priv) | Out-Null
        Write-Host "Creating $priv. A passphrase is optional; if you set one, load it with ssh-add so SshPress can use it." -ForegroundColor Yellow
        & ssh-keygen -t ed25519 -f $priv -C "sshpress@$env:COMPUTERNAME"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath "$priv.pub")) { throw 'ssh-keygen failed.' }
    }
    $pub = (Get-Content -LiteralPath "$priv.pub" -Raw).Trim()
    if ($pub -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-nistp256@openssh\.com) [A-Za-z0-9+/=]+( [^''\r\n]*)?$') {
        throw "$priv.pub does not look like a single OpenSSH public key."
    }
    # The key goes in the command line (single-quoted, validated above), not through stdin: no CRLF surprises.
    $install = "umask 077; mkdir -p ~/.ssh; touch ~/.ssh/authorized_keys; grep -qxF '$pub' ~/.ssh/authorized_keys || printf '%s\n' '$pub' >> ~/.ssh/authorized_keys"
    Write-Host "Installing the public key (enter the password for $Target when asked)..." -ForegroundColor Yellow
    $inst = $common + @('-o', 'StrictHostKeyChecking=yes', '--', $Target, $install)
    & ssh @inst
    if ($LASTEXITCODE -ne 0) { throw "Could not install the key on $Target." }

    $out = (& ssh @probe 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Key installed, but key login still fails:`n$out`nIf the key has a passphrase, start ssh-agent and run ssh-add, or create a key without one."
    }
}

# $true / $false = an Explorer window is / is not showing the folder; $null = can't tell on this system.
function Test-SpExplorerOpen {
    param($Shell, [string]$Root)
    try {
        foreach ($w in $Shell.Windows()) {
            $p = $null
            try { $p = $w.Document.Folder.Self.Path } catch { continue }
            if ($p -and ($p -ieq $Root -or $p.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase))) { return $true }
        }
        return $false
    }
    catch { return $null }
}

function Start-SshPress {
    <#
    .SYNOPSIS
        Shows a folder on an SSH server as a normal folder in Windows Explorer.
    .DESCRIPTION
        Opens an Explorer window on a local folder that mirrors a remote directory, like a file share
        but over SSH. Folders load when you open them and files download when you open them. Copying
        files in, saving, renaming, creating and deleting are sent to the server.

        Uses the Windows cloud files API (the same one OneDrive uses) and the OpenSSH client that ships
        with Windows. Nothing to install. Needs key login; if you don't have one yet it offers to set it
        up (it asks first).

        The command runs until you close the Explorer window, then disconnects and returns.
        It prints nothing unless something goes wrong; use -Verbose (or -v) for progress.
        While it runs: R = refresh from the server, Q = quit.

        Deleting locally deletes it on the server, like any SFTP client (use -RemoteDelete Trash to move
        it to ~/.sshpress-trash instead, or Never to leave the server alone).
    .PARAMETER Target
        user@host, or a Host alias from ~/.ssh/config (user, port, key and ProxyJump are honoured).
    .PARAMETER Path
        Remote folder to show. Default: the remote home directory.
    .PARAMETER Port
        SSH port. Default 22.
    .PARAMETER IdentityFile
        Private key to use. Default: whatever ssh picks (~/.ssh/id_ed25519 etc.).
    .PARAMETER Root
        Local folder to use. Default: %USERPROFILE%\SshPress\<target>. Must be on a local NTFS drive
        and not inside OneDrive.
    .PARAMETER RemoteDelete
        What a local delete does on the server. Remove (default) deletes it (rm -rf), Trash moves it to
        ~/.sshpress-trash, Never leaves the server untouched.
    .PARAMETER Reset
        Delete the local copy of the folder and start fresh. Use if a folder got stuck.
    .PARAMETER Remove
        Unregister and delete the local folder, then exit.
    .EXAMPLE
        sshp me@10.0.0.5
    .EXAMPLE
        SshPress web01 /var/www -Port 2222 -Verbose
    .EXAMPLE
        sshp me@nas -RemoteDelete Never
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Target,
        [Parameter(Position = 1)][string]$Path = '~',
        [ValidateRange(1, 65535)][int]$Port = 22,
        [Alias('Key')][string]$IdentityFile,
        [string]$Root,
        [ValidateSet('Remove', 'Trash', 'Never')][string]$RemoteDelete = 'Remove',
        [switch]$Reset,
        [switch]$Remove
    )
    $ErrorActionPreference = 'Continue'

    # ---- environment ----
    if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'SshPress only runs on Windows.' }
    if ([Environment]::OSVersion.Version.Build -lt 16299) { throw 'SshPress needs Windows 10 version 1709 or later.' }
    if (-not [Environment]::Is64BitProcess) { throw 'Run this in 64-bit PowerShell.' }
    foreach ($tool in 'ssh', 'ssh-keygen') {
        if (-not (Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue)) {
            throw "$tool was not found. Turn on Settings > System > Optional features > OpenSSH Client."
        }
    }

    # ---- inputs ----
    if ($Target -notmatch '^[A-Za-z0-9][A-Za-z0-9._@%+:\[\]-]{0,254}$') {
        throw "Invalid target '$Target'. Use user@host or a Host alias from ~/.ssh/config."
    }
    if ($IdentityFile) { $IdentityFile = (Resolve-Path -LiteralPath $IdentityFile -ErrorAction Stop).ProviderPath }

    $home_ = ([Environment]::GetFolderPath('UserProfile')).TrimEnd('\')
    $managed = Join-Path $home_ 'SshPress'
    if (-not $Root) { $Root = Join-Path $managed ($Target -replace '[^\w.@-]', '_') }
    $Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ($Root.Length -le 3 -or $Root -ieq $home_) { throw "Refusing to use '$Root' as the SshPress folder. Pick a dedicated folder." }
    if ($env:OneDrive -and ($Root -ieq $env:OneDrive.TrimEnd('\') -or $Root.StartsWith($env:OneDrive.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw 'The SshPress folder cannot be inside OneDrive. Use -Root to pick another location.'
    }

    $cf = Get-SpProvider

    if ($Remove -or $Reset) {
        Write-Verbose "Removing $Root"
        [void]$cf::Unregister($Root)
        if (Test-Path -LiteralPath $Root) {
            try { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction Stop }
            catch { Write-Warning "Could not fully clear $Root : $_" }
        }
        if ($Remove) { return }
    }

    # ---- login (host key + key auth), then start ----
    Confirm-SpLogin -Target $Target -Port $Port -Key $IdentityFile

    $mode = @{ Trash = 0; Remove = 1; Never = 2 }[$RemoteDelete]
    $err = $cf::Start($Target, $Port, $IdentityFile, $Root, $Path, $mode)
    if ($err) { throw $err }

    $shell = $null
    try {
        Start-Process explorer.exe -ArgumentList "`"$Root`""
        Write-Verbose "Showing ${Target}:$Path in $Root"
        Write-Verbose 'Close the Explorer window to disconnect. R = refresh from the server, Q = quit.'
        $shell = New-Object -ComObject Shell.Application

        $seen = $false; $missing = 0; $tick = 0
        while ($true) {
            foreach ($line in $cf::Drain()) {
                if ($line -match '  ERROR  ') { Write-Warning ($line -replace '^\S+\s+ERROR\s+', '') } else { Write-Verbose $line }
            }
            if ([Console]::KeyAvailable) {
                $k = [Console]::ReadKey($true)
                if ($k.Key -eq 'Q') { break }
                if ($k.Key -eq 'R') { $cf::Refresh() }
            }
            if (++$tick % 5 -eq 0) {
                $open = Test-SpExplorerOpen -Shell $shell -Root $Root
                if ($open -eq $true) { $seen = $true; $missing = 0 }
                elseif ($open -eq $false -and $seen) { if (++$missing -ge 3) { Write-Verbose 'Explorer window closed.'; break } }
            }
            Start-Sleep -Milliseconds 200
        }
    }
    finally {
        for ($i = 0; $i -lt 150 -and $cf::Pending() -gt 0; $i++) { Start-Sleep -Milliseconds 200 }   # let queued uploads finish
        $cf::Stop()
        foreach ($line in $cf::Drain()) {
            if ($line -match '  ERROR  ') { Write-Warning ($line -replace '^\S+\s+ERROR\s+', '') } else { Write-Verbose $line }
        }
        if ($shell) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
        Write-Verbose 'Disconnected. Files not downloaded yet will not open until you run SshPress again.'
    }
}

Set-Alias -Name SshPress -Value Start-SshPress
Set-Alias -Name sshp -Value Start-SshPress

Export-ModuleMember -Function Start-SshPress -Alias SshPress, sshp
