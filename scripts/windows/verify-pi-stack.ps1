[CmdletBinding()]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { Join-Path $HOME ".ssh\id_ed25519" }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { Join-Path $HOME ".ssh\known_hosts" }),
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

try {
    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    $failures = New-Object System.Collections.Generic.List[string]

    $sshProbe = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand "echo ok"
    if ($sshProbe.ExitCode -eq 0 -and ($sshProbe.Output -join "`n").Contains("ok")) {
        Write-OpResult -Success $true -Step "SSH connectivity" -Details "Pi SSH probe succeeded."
    }
    else {
        $failures.Add("SSH connectivity failed.")
        Write-OpResult -Success $false -Step "SSH connectivity" -Details "Unable to connect to Pi."
    }

    $activeCheck = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand "sudo -n systemctl is-active swedesclantracker-api swedesclantracker-worker nginx"
    $activeLines = @($activeCheck.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne "" })
    $nonActiveLines = @($activeLines | Where-Object { $_ -ne "active" })
    $allActive = $activeCheck.ExitCode -eq 0 -and $activeLines.Count -ge 3 -and $nonActiveLines.Count -eq 0
    if ($allActive) {
        Write-OpResult -Success $true -Step "Service active state" -Details ("States: " + ($activeLines -join ", "))
    }
    else {
        $failures.Add("One or more Pi services are not active.")
        Write-OpResult -Success $false -Step "Service active state" -Details ("States: " + ($activeLines -join ", "))
    }

    $enabledCheck = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand "sudo -n systemctl is-enabled swedesclantracker-api swedesclantracker-worker nginx"
    $enabledLines = @($enabledCheck.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne "" })
    $nonEnabledLines = @($enabledLines | Where-Object { $_ -ne "enabled" })
    $allEnabled = $enabledCheck.ExitCode -eq 0 -and $enabledLines.Count -ge 3 -and $nonEnabledLines.Count -eq 0
    if ($allEnabled) {
        Write-OpResult -Success $true -Step "Service enabled state" -Details ("States: " + ($enabledLines -join ", "))
    }
    else {
        $failures.Add("One or more Pi services are not enabled.")
        Write-OpResult -Success $false -Step "Service enabled state" -Details ("States: " + ($enabledLines -join ", "))
    }

    $remoteApiProbe = 'code=$(/usr/bin/curl -s -o /dev/null -w ''%{http_code}'' http://127.0.0.1:5166/api/dashboard); echo $code'
    $apiCodeCheck = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $remoteApiProbe
    $apiCode = ($apiCodeCheck.Output | Select-Object -First 1).ToString().Trim()
    if ($apiCode -eq "401" -or $apiCode -eq "200") {
        Write-OpResult -Success $true -Step "Pi API localhost probe" -Details "HTTP $apiCode"
    }
    else {
        $failures.Add("Pi API localhost probe returned unexpected code '$apiCode'.")
        Write-OpResult -Success $false -Step "Pi API localhost probe" -Details "HTTP $apiCode"
    }

    try {
        $dashboardResponse = Invoke-WebRequest -Uri "http://$HostOrIp/" -UseBasicParsing -TimeoutSec 10
        if ($dashboardResponse.StatusCode -ge 200 -and $dashboardResponse.StatusCode -lt 400) {
            Write-OpResult -Success $true -Step "Windows LAN dashboard probe" -Details "HTTP $($dashboardResponse.StatusCode)"
        }
        else {
            $failures.Add("Dashboard returned unexpected status $($dashboardResponse.StatusCode).")
            Write-OpResult -Success $false -Step "Windows LAN dashboard probe" -Details "HTTP $($dashboardResponse.StatusCode)"
        }
    }
    catch {
        $failures.Add("Dashboard is not reachable from Windows.")
        Write-OpResult -Success $false -Step "Windows LAN dashboard probe" -Details $_.Exception.Message
    }

    if ($failures.Count -eq 0) {
        Write-OpResult -Success $true -Step "Verification complete" -Details "All checks passed." -NextStep "Proceed with burn-in validation."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    Write-OpResult -Success $false -Step "Verification complete" -Details ($failures -join " | ") -NextStep "Resolve failing checks and rerun verify-pi-stack.ps1."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
catch {
    Write-OpResult -Success $false -Step "Verification error" -Details $_.Exception.Message -NextStep "Confirm SSH credentials and Pi availability."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
