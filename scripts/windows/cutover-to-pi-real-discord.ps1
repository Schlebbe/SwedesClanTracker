[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { Join-Path $HOME ".ssh\id_ed25519" }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { Join-Path $HOME ".ssh\known_hosts" }),
    [string]$DiscordToken,
    [string]$DiscordAdminRoleId,
    [string]$DiscordGuildId,
    [string]$DiscordChannelId,
    [string]$DiscordPetHiscoresChannelId,
    [string]$WindowsApiServiceName = "SwedesClanTracker-Api",
    [string]$WindowsWorkerServiceName = "SwedesClanTracker-Worker",
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

    $target = "Cut over production to Pi ($User@$HostOrIp) and stop Windows services ($WindowsApiServiceName/$WindowsWorkerServiceName)"
    if (-not $PSCmdlet.ShouldProcess($target, "Execute hard handoff")) {
        Write-OpResult -Success $true -Step "Cutover canceled" -Details "No production service changes were made." -NextStep "Rerun script and confirm when ready."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    $setDiscordScript = Join-Path $PSScriptRoot "set-pi-discord-config.ps1"
    & $setDiscordScript `
        -HostOrIp $HostOrIp `
        -User $User `
        -KeyPath $KeyPath `
        -KnownHostsPath $KnownHostsPath `
        -DiscordToken $DiscordToken `
        -DiscordAdminRoleId $DiscordAdminRoleId `
        -DiscordGuildId $DiscordGuildId `
        -DiscordChannelId $DiscordChannelId `
        -DiscordPetHiscoresChannelId $DiscordPetHiscoresChannelId `
        -Confirm:$false `
        -NoPause
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to apply real Discord values on Pi worker."
    }

    Stop-Service -Name $WindowsApiServiceName, $WindowsWorkerServiceName -ErrorAction Stop
    Write-OpResult -Success $true -Step "Windows production services stopped" -Details "$WindowsApiServiceName and $WindowsWorkerServiceName are stopped."

    $apiControl = Join-Path $PSScriptRoot "control-pi-api.ps1"
    $workerControl = Join-Path $PSScriptRoot "control-pi-worker.ps1"
    & $apiControl -Action start -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -Confirm:$false -NoPause
    if ($LASTEXITCODE -ne 0) { throw "Failed to start Pi API service." }
    & $workerControl -Action start -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -Confirm:$false -NoPause
    if ($LASTEXITCODE -ne 0) { throw "Failed to start Pi worker service." }

    $verifyScript = Join-Path $PSScriptRoot "verify-pi-stack.ps1"
    & $verifyScript -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -NoPause
    if ($LASTEXITCODE -ne 0) {
        throw "Pi verification failed after cutover. Run rollback-to-windows.ps1 if needed."
    }

    Write-OpResult -Success $true -Step "Cutover complete" -Details "Pi stack is active with updated Discord values; Windows production services are stopped." -NextStep "Monitor Discord behavior and logs closely for the first hour."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Cutover failed" -Details $_.Exception.Message -NextStep "Run rollback-to-windows.ps1 if production behavior is impacted."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
