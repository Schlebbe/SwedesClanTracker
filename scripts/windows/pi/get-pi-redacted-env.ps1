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

function Redact-Line {
    param([string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line)) { return $Line }
    if ($Line.TrimStart().StartsWith("#")) { return $Line }
    if ($Line -notmatch "=") { return $Line }

    $parts = $Line.Split("=", 2)
    $key = $parts[0].Trim()
    $value = $parts[1]

    $sensitive = @(
        "token",
        "password",
        "secret",
        "connectionstrings__defaultconnection",
        "apikey",
        "privatekey",
        "webhook"
    )
    $normalizedKey = $key.ToLowerInvariant()
    foreach ($fragment in $sensitive) {
        if ($normalizedKey.Contains($fragment)) {
            return "$key=REDACTED"
        }
    }

    return "$key=$value"
}

try {
    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    $remoteCommand = "sudo -n sh -c 'echo ""### /etc/swedesclantracker/api.env""; cat /etc/swedesclantracker/api.env; echo; echo ""### /etc/swedesclantracker/worker.env""; cat /etc/swedesclantracker/worker.env'"

    $result = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $remoteCommand
    if ($result.ExitCode -ne 0) {
        throw "Failed to read env files with sudo (exit $($result.ExitCode))."
    }

    $redacted = @()
    foreach ($line in $result.Output) {
        $redacted += (Redact-Line -Line "$line")
    }

    $redacted | Out-Host
    Write-OpResult -Success $true -Step "Redacted env snapshot" -Details "Printed sanitized api.env + worker.env values." -NextStep "Use for diagnostics only; never paste raw secrets."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Redacted env snapshot failed" -Details $_.Exception.Message -NextStep "Confirm sudo rights (NOPASSWD) and file paths."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
