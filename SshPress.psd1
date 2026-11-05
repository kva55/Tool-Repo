@{
    RootModule           = 'SshPress.psm1'
    ModuleVersion        = '0.1.0'
    GUID                 = '4ce23486-c2bf-4094-927d-ee65e7af6996'
    Author               = 'SshPress contributors'
    Copyright            = '(c) 2026 SshPress contributors. MIT License.'
    Description          = 'Show an SSH server folder as a normal folder in Windows Explorer, using the Windows cloud files API and the built-in OpenSSH client. Nothing to install.'
    PowerShellVersion    = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport    = @('Start-SshPress')
    AliasesToExport      = @('SshPress', 'sshp')
    CmdletsToExport      = @()
    VariablesToExport    = @()
    FileList             = @('SshPress.psd1', 'SshPress.psm1', 'lib/SshPress.Cf.cs')
    PrivateData          = @{
        PSData = @{
            Tags         = @('ssh', 'sftp', 'explorer', 'cloud-files', 'file-transfer', 'Windows')
            LicenseUri   = 'https://github.com/OWNER/SshPress/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/OWNER/SshPress'
            ReleaseNotes = 'First release.'
        }
    }
}
