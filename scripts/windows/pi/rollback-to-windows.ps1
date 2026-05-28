[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [string]$WindowsApiServiceName = "SwedesClanTracker-Api",
    [string]$WindowsWorkerServiceName = "SwedesClanTracker-Worker",
    [switch]$StopPiApi,
    [switch]$NoPause,
    [switch]$ElevatedRelaunch
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

function Ensure-ElevatedOrRelaunch {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        return
    }
    if ($ElevatedRelaunch) {
        throw "Script requires Administrator rights."
    }

    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-NoPause", "-ElevatedRelaunch")
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        if ($entry.Key -eq "NoPause" -or $entry.Key -eq "ElevatedRelaunch") {
            continue
        }
        if ($entry.Value -is [System.Management.Automation.SwitchParameter]) {
            if (-not $entry.Value.IsPresent) {
                continue
            }
            $argList += "-$($entry.Key)"
            continue
        }
        $argList += "-$($entry.Key)"
        $argList += "$($entry.Value)"
    }

    $proc = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $argList -Wait -PassThru
    $proc.WaitForExit()
    exit $proc.ExitCode
}

try {
    Ensure-ElevatedOrRelaunch

    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    $target = "Rollback production to Windows services and stop Pi worker"
    if (-not $PSCmdlet.ShouldProcess($target, "Execute rollback")) {
        Write-OpResult -Success $true -Step "Rollback canceled" -Details "No service state changes were made." -NextStep "Rerun script and confirm to rollback."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    $workerControl = Join-Path $PSScriptRoot "control-pi-worker.ps1"
    & $workerControl -Action stop -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -Confirm:$false -NoPause
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to stop Pi worker service."
    }

    if ($StopPiApi) {
        $apiControl = Join-Path $PSScriptRoot "control-pi-api.ps1"
        & $apiControl -Action stop -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -Confirm:$false -NoPause
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to stop Pi API service."
        }
    }

    Start-Service -Name $WindowsApiServiceName, $WindowsWorkerServiceName -ErrorAction Stop

    $windowsStates = Get-Service -Name $WindowsApiServiceName, $WindowsWorkerServiceName | Select-Object Name, Status
    $details = ($windowsStates | ForEach-Object { "$($_.Name)=$($_.Status)" }) -join ", "
    Write-OpResult -Success $true -Step "Rollback complete" -Details $details -NextStep "Validate Windows production behavior and keep Pi worker stopped until issue is resolved."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Rollback failed" -Details $_.Exception.Message -NextStep "Manually check Windows and Pi service states, then retry rollback."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
