# SshPress

Show a folder on an SSH server as a **normal folder in Windows Explorer**, like a file share, but over SSH.
Pure PowerShell + the OpenSSH client that ships with Windows. **Nothing to install, no drivers, no admin rights.**

```powershell
sshp me@10.0.0.5                 # or: SshPress me@10.0.0.5
sshp me@nas /mnt/data -Port 2222
```

An Explorer window opens on the server's folder. Open a folder and it loads, open a file and it downloads.
Copy files in, save, rename, create or delete, and the change is sent to the server. Close the Explorer window and
SshPress disconnects and exits.

## Install

```powershell
git clone https://github.com/OWNER/SshPress
cd SshPress
.\install.ps1            # current user, no admin; -AllEditions for both Windows PowerShell and PowerShell 7
```

Open a new PowerShell window. `sshp` and `SshPress` now work in every session (the module autoloads).
Help: `Get-Help sshp -Full`. Remove: `.\install.ps1 -Uninstall`.

Needs: Windows 10 1709+ (or 11), 64-bit PowerShell 5.1 or 7, the *OpenSSH Client* optional feature (on by default),
and a local NTFS drive.

## Use

| | |
|---|---|
| `sshp user@host [path]` | Mirror `path` (default: home) in Explorer. `host` can be an alias from `~/.ssh/config`. |
| `-Port 2222` `-IdentityFile key` | Non-default port / key. Everything else (user, ProxyJump...) comes from `~/.ssh/config`. |
| `-RemoteDelete Remove\|Trash\|Never` | What a local delete does on the server. **Default `Remove`** deletes it, like any SFTP client. `Trash` moves it to `~/.sshpress-trash`; `Never` leaves the server alone. |
| `-Root C:\some\folder` | Local folder to use (default `%USERPROFILE%\SshPress\<target>`; local NTFS, not inside OneDrive). |
| `-Verbose` / `-v` | Show progress. Without it SshPress is silent unless something goes wrong. |
| `-Reset` / `-Remove` | Wipe the local copy of the folder and start over / unregister and delete it, then exit. |

While it runs: **R** refreshes from the server, **Q** quits. Closing the Explorer window also quits (a few seconds' grace).
The command runs in the foreground; use another terminal tab or `Start-Process` if you want your prompt back.

First connection to a host: SSH shows the host fingerprint and you confirm it. If you have no key login yet,
SshPress offers (asks first) to create an ed25519 key and install the public key; you type the password once.

## How it works

```
Explorer  <->  local folder (placeholders)  <->  Windows cloud files driver (cldflt)
                                                      |  callbacks
                                              SshPress provider (C#, compiled by Add-Type)
                                                      |  ssh.exe  (one short connection per action)
                                                  your server  (needs only sh, cat, find or ls)
```

- The local folder is registered as a **sync root** through `cldapi.dll`, the same Windows API OneDrive uses.
  Remote entries show up as *placeholders* (no data on disk).
- Opening a folder triggers a **FETCH_PLACEHOLDERS** callback: the provider lists the remote directory over ssh.
  Opening a file triggers **FETCH_DATA**: the provider streams `cat` over ssh into the file.
- A `FileSystemWatcher` sees local creates, saves, renames and deletes and queues them. A single worker uploads
  (temp file then rename, keeping remote permissions), renames with `mv` (replaces an existing target, like SFTP), and deletes.
- Everything on the server is plain `sh`, `cat`, `mv`, `mkdir`, `find`/`ls`. Nothing is installed there.
- The PowerShell module is a thin wrapper (login flow, validation, Explorer-close detection). The engine is `SshPress/lib/SshPress.Cf.cs`.

## Security notes

Reviewed, and what was done about it:

- **No shell injection.** Every path sent to the server is single-quoted (`'\''` escaping) and run under `sh -c`.
  Tested with names like `it's` and `$(touch PWNED) \`id\` ; x`.
- **No ssh option injection.** Targets must match `^[A-Za-z0-9][A-Za-z0-9._@%+:\[\]-]*$` and ssh is called with `--`
  before the target, so `-oProxyCommand=...` cannot be smuggled in.
- **Host keys are never auto-trusted.** Background connections use `StrictHostKeyChecking=yes` and `BatchMode=yes`
  (they can never prompt, so they can't hang). A new host is confirmed by you in the console; a *changed* key is a hard stop.
- **A hostile or buggy server can't escape the folder.** Names containing `\ / : * ? " < > |`, control characters,
  `.`/`..`, reserved device names (`CON`, `NUL`...), trailing dots/spaces, or differing only by case are skipped. Listings are capped at 100,000 entries.
- **Server text can't inject terminal escape codes**: control characters are stripped from everything logged.
- **Remote paths can't escape the mirrored folder**: they are derived from the local path and `..` segments are rejected.
- **Files stay fully editable.** Saving, overwriting, renaming over an existing file and deleting all work as in any SFTP client.
  If you want a safety net, `-RemoteDelete Trash` moves deletes to `~/.sshpress-trash` (mode 700) instead.
- `-Reset`/`-Remove` delete the local folder you point `-Root` at, but refuse a drive root or your user profile.
- **Key setup asks first**, puts the validated public key in the command line (no stdin/CRLF issues), is idempotent, and creates
  `~/.ssh` as 700 / `authorized_keys` as 600. SshPress never stores or transmits passwords.
- Callbacks into native code are wrapped so a managed exception can't crash the PowerShell process.

Things to know:

- A generated key may have **no passphrase** (your choice at the `ssh-keygen` prompt). If you set one, load it with `ssh-agent`/`ssh-add`
  or SshPress can't use it (it never prompts in the background).
- **Downloaded files stay on your disk** (under the local folder) until you `-Remove` it or Windows dehydrates them. Treat that folder like any local copy of the data.
- Local deletes propagate. That's the point of a file share. Use `-RemoteDelete Trash` for a safety net, or `Never` for a read-mostly mirror.
- Trust the server you connect to: file contents are whatever it returns.
- The module is unsigned. Read it (it's ~250 lines of PowerShell and one C# file) before you run it.

## Limitations

- It's a folder, not a drive letter, and uploads happen a second or two after you save.
- One new SSH connection per action, so pasting thousands of tiny files is slow.
- Remote file names Windows can't represent are skipped (and logged with `-v`).
- Remote changes appear when you press **R** (there is no server-side change watching).
- Whole-file downloads only (no partial hydration); very large files download fully on open.
- Windows only. The SSH data path is covered by automated checks on Linux; the cloud-files layer has only been exercised by hand on Windows 11. Expect rough edges and please file issues.

## Prior art

SshPress isn't the first way to get SFTP into Explorer, and not the first to use the Cloud Files API:

- **[SSH Folders](https://apps.microsoft.com/detail/9mvv9hwgpj9m)** (Microsoft Store): SFTP server as on-demand placeholder folders via the Windows Cloud Filter API; native libssh2 transport.
- **[ssh-cloud](https://github.com/mevdschee/ssh-cloud)**: Go app with its own Explorer-like window over SFTP (MSI install).
- **[Mountain Duck](https://mountainduck.io)**: commercial; advertises Cloud Files API on Windows.
- **[SSHFS-Win](https://github.com/winfsp/sshfs-win)** / **rclone mount**: free, real drive letter, but need the WinFsp driver installed.
- **RaiDrive**, **ExpanDrive**, **SFTP Net Drive**: commercial/freemium drive mappers. **WinSCP**: client with an Explorer-style UI.

What SshPress adds: a few hundred lines of readable PowerShell + C#, using only the OpenSSH client already in Windows. No installer, no driver, no
admin rights, no bundled SSH library. Microsoft's [Cloud Files API docs](https://learn.microsoft.com/windows/win32/cfapi/build-a-cloud-file-sync-engine) describe the platform.

## Troubleshooting

| Symptom | Try |
|---|---|
| "CfRegisterSyncRoot failed 0x8007..." | Folder must be on a local NTFS drive and not inside OneDrive. Use `-Root`. |
| Folder shows "cloud operation was unsuccessful" | Run with `-v` and read the `ERROR` line. Run `sshp <target> -Reset` to clear a stuck folder. |
| Stuck on login | Run `ssh user@host` once yourself to confirm the host key and key login. |
| Nothing appears after a server change | Press **R** in the SshPress terminal. |
| Execution policy error on import | `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`, then run `install.ps1` again (it unblocks the files). |

## License

MIT, see [LICENSE](LICENSE).
