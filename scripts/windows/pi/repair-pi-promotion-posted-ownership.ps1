[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$Database = "swedesclantracker",
    [string]$RemoteDbUser = "postgres",
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

    $sqlPath = Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..\..\sql")).Path "repair-promotion-posted-player-ownership.sql"
    if (-not (Test-Path -LiteralPath $sqlPath)) {
        throw "Missing SQL file: $sqlPath"
    }

    $mode = if ($Apply) { "apply" } else { "dry-run" }
    $remoteSqlPath = "/tmp/repair-promotion-posted-player-ownership.sql"
    $upload = Invoke-ScpUpload -LocalPath $sqlPath -RemotePath $remoteSqlPath -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath
    if ($upload.ExitCode -ne 0) {
        throw "Failed to upload SQL script to Pi."
    }

    $applyFlag = if ($Apply) { "1" } else { "0" }
    $remoteCommand = @"
sudo -n -u $RemoteDbUser psql -v ON_ERROR_STOP=1 -v apply=$applyFlag -d $Database -f '$remoteSqlPath'
sudo -n rm -f '$remoteSqlPath'
"@

    Write-OpResult -Success $true -Step "Running repair ($mode)" -Details "Executing SQL maintenance script on Pi."
    $exec = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $remoteCommand
    if ($exec.Output) {
        $exec.Output | Out-Host
    }
    if ($exec.ExitCode -ne 0) {
        throw "Repair script exited with code $($exec.ExitCode)."
    }

    $next = if ($Apply) { "Re-run this script without -Apply for post-fix verification." } else { "Review dry-run output, then rerun with -Apply to execute." }
    Write-OpResult -Success $true -Step "Repair script completed" -Details "Mode: $mode" -NextStep $next
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Repair script failed" -Details $_.Exception.Message -NextStep "Confirm sudo/psql access and retry."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
