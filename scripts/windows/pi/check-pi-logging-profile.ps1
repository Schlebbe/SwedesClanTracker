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

    $remoteCommand = "sudo -n sh -c 'grep -h -E ""^Logging__LogLevel__Microsoft\\.EntityFrameworkCore\\.Database\\.Command="" /etc/swedesclantracker/api.env /etc/swedesclantracker/worker.env || true'"
    $result = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $remoteCommand
    if ($result.ExitCode -ne 0) {
        throw "Failed to inspect logging profile (exit $($result.ExitCode))."
    }

    $values = @($result.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne "" })
    if ($values.Count -eq 0) {
        Write-OpResult -Success $true -Step "EF command logging profile" -Details "No explicit EF command log level set; production default recommendation is Warning unless debugging."
    }
    else {
        $values | Out-Host
        $hasVerbose = $values | Where-Object { $_ -match '=Information$|=Debug$|=Trace$' }
        if ($hasVerbose) {
            Write-OpResult -Success $false -Step "EF command logging profile" -Details "Verbose EF SQL logging is enabled. Recommended: set to Warning outside debug windows." -NextStep "Temporarily enable verbose SQL logging only during focused debugging."
        }
        else {
            Write-OpResult -Success $true -Step "EF command logging profile" -Details "EF SQL logging is not in verbose mode."
        }
    }

    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Logging profile check failed" -Details $_.Exception.Message -NextStep "Confirm sudo access and retry."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
