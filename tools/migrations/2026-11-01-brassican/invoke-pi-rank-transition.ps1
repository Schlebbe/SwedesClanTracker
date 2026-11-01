[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet("plan", "verify", "apply", "rollback", "discord-report", "member-report")]
    [string]$Command = "plan",
    [string]$ManifestFileName = "brassican-transition.json",
    [string]$ToolPath = "/opt/swedesclantracker/tools/rank-transition",
    [string]$WorkerAppSettingsPath = "/opt/swedesclantracker/worker/appsettings.json",
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [string]$ReviewedBy = "",
    [switch]$MaintenanceConfirmed,
    [switch]$BackupConfirmed,
    [switch]$GameReconciled,
    [switch]$NoDiscord,
    [switch]$NoWom,
    [switch]$ApproveQualifyingPromotions,
    [switch]$NoApproveQualifyingPromotions,
    [switch]$AutomaticDiscordMembers,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
. (Join-Path $repositoryRoot "scripts/windows/pi/pi-common.ps1")

function ConvertTo-Base64Utf8 {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}

function Assert-ManifestFileName {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value -ne [IO.Path]::GetFileName($Value) -or
        $Value.Contains("/") -or
        $Value.Contains("\") -or
        $Value -in @(".", "..")) {
        throw "ManifestFileName must be a single file name stored under the remote SSH user's private .rank-transition directory."
    }
}

function New-TransitionArguments {
    param(
        [Parameter(Mandatory = $true)][string]$SelectedCommand,
        [Parameter(Mandatory = $true)][string]$SelectedManifest
    )

    $args = [System.Collections.Generic.List[string]]::new()
    $args.Add($SelectedCommand)
    $args.Add("--manifest")
    # The remote Python payload resolves this file name under the SSH user's
    # private ~/.rank-transition directory after it has loaded worker.env.
    $args.Add($SelectedManifest)

    if ($SelectedCommand -in @("apply", "rollback")) {
        $args.Add("--reviewed-by")
        $args.Add($ReviewedBy)
        if ($MaintenanceConfirmed) { $args.Add("--maintenance-confirmed") }
        if ($BackupConfirmed) { $args.Add("--backup-confirmed") }
        if ($GameReconciled) { $args.Add("--game-reconciled") }
    }
    elseif ($SelectedCommand -eq "plan") {
        if ($NoDiscord) { $args.Add("--no-discord") }
        if ($NoWom) { $args.Add("--no-wom") }
        if ($ApproveQualifyingPromotions -or -not $NoApproveQualifyingPromotions) {
            $args.Add("--approve-qualifying-promotions")
        }
        if ($AutomaticDiscordMembers) { $args.Add("--automatic-discord-members") }
    }

    return $args.ToArray()
}

try {
    Assert-ManifestFileName -Value $ManifestFileName
    if ($Command -in @("apply", "rollback")) {
        if ([string]::IsNullOrWhiteSpace($ReviewedBy) -or
            -not $MaintenanceConfirmed -or
            -not $BackupConfirmed -or
            -not $GameReconciled) {
            throw "Explicit $Command requires -ReviewedBy, -MaintenanceConfirmed, -BackupConfirmed, and -GameReconciled."
        }
    }
    if (($NoDiscord -or $NoWom) -and $Command -ne "plan") {
        throw "-NoDiscord and -NoWom are diagnostic plan options only."
    }
    if (($ApproveQualifyingPromotions -or $NoApproveQualifyingPromotions) -and $Command -ne "plan") {
        throw "-ApproveQualifyingPromotions and -NoApproveQualifyingPromotions are plan options only."
    }
    if ($AutomaticDiscordMembers -and $Command -ne "plan") {
        throw "-AutomaticDiscordMembers is a plan option only; standard apply/rollback uses the frozen manifest policy."
    }
    if ($ApproveQualifyingPromotions -and $NoApproveQualifyingPromotions) {
        throw "-ApproveQualifyingPromotions and -NoApproveQualifyingPromotions cannot be used together."
    }

    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    $arguments = New-TransitionArguments -SelectedCommand $Command -SelectedManifest $ManifestFileName
    $payload = @{
        toolPath = $ToolPath
        workerAppSettingsPath = $WorkerAppSettingsPath
        arguments = $arguments
    } | ConvertTo-Json -Compress
    $payloadB64 = ConvertTo-Base64Utf8 -Value $payload

    # The script is sent as base64 so paths, attestations, and manifest names
    # never become shell syntax. It reads worker.env through sudo cat, parses
    # KEY=VALUE pairs without eval/source, and execs the already-published
    # transition package with the environment held only in the remote process.
    $python = @'
import base64
import json
import os
from pathlib import Path
import subprocess
import sys

payload = json.loads(base64.b64decode(sys.argv[1]).decode("utf-8"))
tool_path = Path(payload["toolPath"])
worker_appsettings_path = Path(payload["workerAppSettingsPath"])
arguments = [str(value) for value in payload["arguments"]]

if not tool_path.exists():
    print(f"Rank transition package not found: {tool_path}", file=sys.stderr)
    raise SystemExit(2)

if tool_path.is_file():
    work_dir = tool_path.parent
    if tool_path.suffix == ".dll":
        command = ["dotnet", str(tool_path)]
    elif os.access(tool_path, os.X_OK):
        command = [str(tool_path)]
    else:
        print(f"Rank transition package is not executable: {tool_path}", file=sys.stderr)
        raise SystemExit(2)
else:
    work_dir = tool_path
    candidates = [
        tool_path / "SwedesClanTracker.RankTransition",
        tool_path / "rank-transition",
        tool_path / "SwedesClanTracker.RankTransition.dll",
    ]
    command = None
    for candidate in candidates:
        if not candidate.is_file():
            continue
        if candidate.suffix == ".dll":
            command = ["dotnet", str(candidate)]
        elif os.access(candidate, os.X_OK):
            command = [str(candidate)]
        else:
            print(f"Rank transition package executable is not executable: {candidate}", file=sys.stderr)
            raise SystemExit(2)
        break
    if command is None:
        print(f"Rank transition executable is missing from package: {tool_path}", file=sys.stderr)
        raise SystemExit(2)

worker_settings_result = subprocess.run(
    ["sudo", "-n", "cat", str(worker_appsettings_path)],
    check=False,
    capture_output=True,
)
if worker_settings_result.returncode != 0:
    print(f"Could not read production Worker appsettings at {worker_appsettings_path} with sudo.", file=sys.stderr)
    raise SystemExit(worker_settings_result.returncode or 3)
try:
    worker_settings = json.loads(worker_settings_result.stdout.decode("utf-8-sig"))
except (UnicodeDecodeError, json.JSONDecodeError):
    print(f"Production Worker appsettings is not valid UTF-8 JSON: {worker_appsettings_path}", file=sys.stderr)
    raise SystemExit(3)

env = dict(os.environ)
wise_settings = worker_settings.get("WiseOldMan") or {}
if "WiseOldMan__GroupId" not in env and "GroupId" in wise_settings and wise_settings["GroupId"] is not None:
    env["WiseOldMan__GroupId"] = str(wise_settings["GroupId"])

env_result = subprocess.run(
    ["sudo", "-n", "cat", "/etc/swedesclantracker/worker.env"],
    check=False,
    capture_output=True,
)
if env_result.returncode != 0:
    print("Could not read /etc/swedesclantracker/worker.env with sudo.", file=sys.stderr)
    raise SystemExit(env_result.returncode or 3)

for raw_line in env_result.stdout.decode("utf-8-sig").splitlines():
    line = raw_line.strip()
    if not line or line.startswith("#") or "=" not in line:
        continue
    key, value = line.split("=", 1)
    key = key.strip()
    if not key or any(character not in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_" for character in key):
        continue
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        value = value[1:-1]
    env[key] = value

manifest_path = Path.home() / ".rank-transition"
manifest_path.mkdir(mode=0o700, parents=True, exist_ok=True)
os.chmod(manifest_path, 0o700)
for index, argument in enumerate(arguments[:-1]):
    if argument == "--manifest":
        manifest_name = Path(arguments[index + 1])
        if manifest_name.name != arguments[index + 1] or manifest_name.name in ("", ".", ".."):
            print("Manifest name must remain inside the SSH user's private .rank-transition directory.", file=sys.stderr)
            raise SystemExit(2)
        arguments[index + 1] = str(manifest_path / manifest_name.name)
        break
env["RankTransition__ManifestDirectory"] = str(manifest_path)
os.chdir(work_dir)
try:
    os.execvpe(command[0], command + arguments, env)
except OSError as error:
    print(f"Could not execute rank transition package: {error}", file=sys.stderr)
    raise SystemExit(2)
'@
    $pythonB64 = ConvertTo-Base64Utf8 -Value $python
    $remoteCommand = @"
set -eu
python3 -c `"`$(printf '%s' '$pythonB64' | base64 -d)`" '$payloadB64'
"@

    if (-not $PSCmdlet.ShouldProcess("$User@$HostOrIp", "Run rank transition '$Command' using the already-published Pi package")) {
        Write-OpResult -Success $true -Step "Pi rank transition preview" -Details "WhatIf: no SSH command was sent and no remote state changed." -NextStep "Rerun without -WhatIf after reviewing the command inputs."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    $result = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $remoteCommand
    if ($result.Output) { $result.Output | Out-Host }
    if ($result.ExitCode -ne 0) {
        Write-OpResult -Success $false -Step "Pi rank transition '$Command' failed" -Details "Remote exit code: $($result.ExitCode)" -NextStep "Review the remote error above; no service was started, stopped, or deployed by this wrapper."
        Pause-IfRequested -NoPause:$NoPause
        exit $result.ExitCode
    }

    Write-OpResult -Success $true -Step "Pi rank transition '$Command' completed" -Details "The published tool ran with its private manifest under the SSH user's home directory." -NextStep "Review the manifest/report before proceeding to the next maintenance gate."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Pi rank transition wrapper error" -Details $_.Exception.Message -NextStep "Check SSH inputs and confirm the published transition package exists on the Pi."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
