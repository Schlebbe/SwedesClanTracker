[CmdletBinding()]
param(
    [ValidateSet("grant", "revoke", "status")]
    [string]$Action = "status",
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$SudoSubjectUser = $(if ($env:PI_SUDO_SUBJECT_USER) { $env:PI_SUDO_SUBJECT_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

try {
    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    if ($SudoSubjectUser -notmatch '^[a-z_][a-z0-9_-]*$') {
        throw "Invalid SudoSubjectUser: '$SudoSubjectUser'."
    }

    $sudoersFile = "/etc/sudoers.d/swedesclantracker-codex-$SudoSubjectUser"
    if ($Action -eq "status") {
        $statusCmd = "sudo -n sh -c 'if [ -r ""$sudoersFile"" ]; then echo present; cat ""$sudoersFile""; else echo missing; fi'"
        $status = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $statusCmd
        if ($status.Output) { $status.Output | Out-Host }
        if ($status.ExitCode -eq 0) {
            Write-OpResult -Success $true -Step "Sudoers status read" -Details "Checked $sudoersFile"
        }
        else {
            Write-OpResult -Success $false -Step "Sudoers status read failed" -Details "Exit code: $($status.ExitCode)" -NextStep "Confirm current sudo privileges."
            Pause-IfRequested -NoPause:$NoPause
            exit 1
        }
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    if ($Action -eq "grant") {
        $grantCmd = @"
tmp_file=/tmp/swedesclantracker-codex-sudoers
printf '%s ALL=(ALL) NOPASSWD:ALL\n' '$SudoSubjectUser' > `"`$tmp_file`"
sudo -n install -m 0440 `"`$tmp_file`" '$sudoersFile'
rm -f `"`$tmp_file`"
sudo -n visudo -cf '$sudoersFile'
"@
        $grant = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $grantCmd
        if ($grant.Output) { $grant.Output | Out-Host }
        if ($grant.ExitCode -ne 0) {
            throw "Grant failed with exit code $($grant.ExitCode)."
        }
        Write-OpResult -Success $true -Step "Sudo access granted" -Details "Installed $sudoersFile for $SudoSubjectUser."
    }
    else {
        $revokeCmd = "sudo -n rm -f '$sudoersFile' && echo removed"
        $revoke = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $revokeCmd
        if ($revoke.Output) { $revoke.Output | Out-Host }
        if ($revoke.ExitCode -ne 0) {
            throw "Revoke failed with exit code $($revoke.ExitCode)."
        }
        Write-OpResult -Success $true -Step "Sudo access revoked" -Details "Removed $sudoersFile."
    }

    Write-OpResult -Success $true -Step "Sudo access update complete" -Details "Action '$Action' finished." -NextStep "Run with -Action status to verify."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Sudo access update failed" -Details $_.Exception.Message -NextStep "Confirm SSH + existing sudo rights and retry."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
