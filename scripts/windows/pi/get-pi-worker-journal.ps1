[CmdletBinding()]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [string]$Since = "30 minutes ago",
    [string]$Until = "",
    [string[]]$Pattern = @("Discord slash", "discord-guess", "Failed handling", "Exception", "Timeout"),
    [int]$Lines = 200,
    [switch]$IncludeEfCommands,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

try {
    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    $payload = @{
        since = $Since
        until = $Until
        patterns = @($Pattern)
        lines = [Math]::Max(1, $Lines)
        includeEfCommands = [bool]$IncludeEfCommands
    } | ConvertTo-Json -Compress

    $payloadB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
    $python = @"
import base64
import json
import subprocess
import sys

payload = json.loads(base64.b64decode("$payloadB64").decode("utf-8"))
cmd = [
    "sudo", "-n", "journalctl",
    "-u", "swedesclantracker-worker",
    "--since", payload["since"],
    "--no-pager",
]
if payload.get("until"):
    cmd.extend(["--until", payload["until"]])

proc = subprocess.run(cmd, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
patterns = [p.lower() for p in payload.get("patterns", []) if p]
include_ef = bool(payload.get("includeEfCommands"))
max_lines = int(payload.get("lines", 200))
matches = []

for line in proc.stdout.splitlines():
    if not include_ef and "EntityFrameworkCore.Database.Command" in line:
        continue
    if patterns and not any(pattern in line.lower() for pattern in patterns):
        continue
    matches.append(line)

for line in matches[-max_lines:]:
    print(line)

print(f"matches={len(matches)} shown={min(len(matches), max_lines)} journal_exit={proc.returncode}")
sys.exit(proc.returncode)
"@
    $pythonB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($python))
    $remoteCommand = "printf '%s' '$pythonB64' | base64 -d | python3"

    $result = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $remoteCommand
    if ($result.Output) {
        $result.Output | Out-Host
    }

    if ($result.ExitCode -ne 0) {
        Write-OpResult -Success $false -Step "Worker journal query failed" -Details "Exit code: $($result.ExitCode)" -NextStep "Retry once; if SSH reset repeats, run test-pi-ssh.ps1."
        Pause-IfRequested -NoPause:$NoPause
        exit 1
    }

    Write-OpResult -Success $true -Step "Worker journal query completed" -Details "Since='$Since', Lines=$Lines"
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Worker journal query error" -Details $_.Exception.Message -NextStep "Run test-pi-ssh.ps1 and verify sudo journal access."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
