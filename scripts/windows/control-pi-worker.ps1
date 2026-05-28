[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet("start", "stop", "restart", "status")]
    [string]$Action,
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { Join-Path $HOME ".ssh\id_ed25519" }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { Join-Path $HOME ".ssh\known_hosts" }),
    [string]$ServiceName = "swedesclantracker-worker",
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

try {
    if ([string]::IsNullOrWhiteSpace($Action)) {
        $Action = Read-ServiceAction -DefaultAction "status"
    }

    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    $needsConfirmation = $Action -ne "status"
    $targetLabel = "$ServiceName on $User@$HostOrIp"
    if ($needsConfirmation -and -not $PSCmdlet.ShouldProcess($targetLabel, $Action)) {
        Write-OpResult -Success $true -Step "Worker action canceled" -Details "No changes made." -NextStep "Run script again and confirm to execute '$Action'."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    $remoteCommand = switch ($Action) {
        "start" { "sudo -n systemctl start $ServiceName && sudo -n systemctl is-active $ServiceName" ; break }
        "stop" { "sudo -n systemctl stop $ServiceName; sudo -n systemctl is-active $ServiceName || true" ; break }
        "restart" { "sudo -n systemctl restart $ServiceName && sudo -n systemctl is-active $ServiceName" ; break }
        "status" { "sudo -n systemctl --no-pager --full status $ServiceName | head -n 12" ; break }
        default { throw "Unsupported action: $Action" }
    }

    $result = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $remoteCommand
    if ($result.Output) {
        $result.Output | Out-Host
    }

    if ($result.ExitCode -ne 0) {
        Write-OpResult -Success $false -Step "Worker action failed" -Details "Action '$Action' exited with code $($result.ExitCode)." -NextStep "Run '-Action status' to inspect service state."
        Pause-IfRequested -NoPause:$NoPause
        exit 1
    }

    Write-OpResult -Success $true -Step "Worker action succeeded" -Details "Action '$Action' applied to $ServiceName." -NextStep "Use '-Action status' to inspect live logs/state if needed."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Worker control error" -Details $_.Exception.Message -NextStep "Verify SSH key/host settings and rerun."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
