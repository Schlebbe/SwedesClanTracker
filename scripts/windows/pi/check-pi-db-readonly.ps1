[CmdletBinding()]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$DbUser = "codex_ro",
    [string]$Database = "swedesclantracker",
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

    Write-Host "Running SSH probe against $User@$HostOrIp..."
    $sshProbeScript = Join-Path $PSScriptRoot "test-pi-ssh.ps1"
    & $sshProbeScript -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -NoPause
    if ($LASTEXITCODE -ne 0) {
        throw "SSH probe failed."
    }

    $remoteCommand = "psql -w -h 127.0.0.1 -U $DbUser -d $Database -At -c 'select current_database(), current_user, now();'"
    $readOnlyResult = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $remoteCommand
    if ($readOnlyResult.Output) {
        $readOnlyResult.Output | Out-Host
    }

    if ($readOnlyResult.ExitCode -ne 0) {
        Write-OpResult -Success $false -Step "Read-only PostgreSQL check failed" -Details "Exit code: $($readOnlyResult.ExitCode)" -NextStep "Verify codex_ro credentials and pg_hba configuration."
        Pause-IfRequested -NoPause:$NoPause
        exit 1
    }

    Write-OpResult -Success $true -Step "Read-only PostgreSQL check succeeded" -Details "Database '$Database' is reachable as '$DbUser'." -NextStep "Proceed with deploy or verification workflow."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Pi DB read-only check error" -Details $_.Exception.Message -NextStep "Confirm SSH and DB role setup, then rerun."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
