[CmdletBinding()]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
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

    $probe = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand "echo ok"
    if ($probe.Output) {
        $probe.Output | Out-Host
    }

    if ($probe.ExitCode -ne 0) {
        Write-OpResult -Success $false -Step "Pi SSH probe failed" -Details "Exit code: $($probe.ExitCode)" -NextStep "Verify host, key, known_hosts, and sudoers setup."
        Pause-IfRequested -NoPause:$NoPause
        exit 1
    }

    Write-OpResult -Success $true -Step "Pi SSH probe succeeded" -Details "SSH connection to $User@$HostOrIp is working." -NextStep "Run check-pi-db-readonly.ps1 or verify-pi-stack.ps1."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Pi SSH probe error" -Details $_.Exception.Message -NextStep "Confirm local key files and Pi host reachability."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
