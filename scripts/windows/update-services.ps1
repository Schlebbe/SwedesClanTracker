[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$RepoRoot = "",
    [string]$PublishRoot = "",
    [string]$Configuration = "Release",
    [string]$ApiServiceName = "SwedesClanTracker-Api",
    [string]$WorkerServiceName = "SwedesClanTracker-Worker",
    [switch]$NoPause,
    [switch]$ElevatedRelaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script in an elevated PowerShell session (Run as Administrator)."
    }
}

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

    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    }
    if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
        $PublishRoot = Join-Path $RepoRoot "deploy"
    }

    if (-not $PSCmdlet.ShouldProcess("$ApiServiceName, $WorkerServiceName", "Stop services, publish, and start services")) {
        Write-OpResult -Success $true -Step "Service update canceled" -Details "No local service changes were made." -NextStep "Rerun and confirm when ready."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }
    Assert-Admin

    Stop-Service -Name $ApiServiceName, $WorkerServiceName -ErrorAction Stop
    Write-OpResult -Success $true -Step "Windows services stopped" -Details "$ApiServiceName and $WorkerServiceName"

    $publishScript = Join-Path $PSScriptRoot "publish-release.ps1"
    & $publishScript -Configuration $Configuration -RepoRoot $RepoRoot -OutputRoot $PublishRoot -NoPause
    if ($LASTEXITCODE -ne 0) {
        throw "Publish step failed."
    }

    Start-Service -Name $ApiServiceName, $WorkerServiceName -ErrorAction Stop
    $serviceStates = Get-Service -Name $ApiServiceName, $WorkerServiceName | Select-Object Name, Status
    $details = ($serviceStates | ForEach-Object { "$($_.Name)=$($_.Status)" }) -join ", "
    Write-OpResult -Success $true -Step "Windows service update completed" -Details $details -NextStep "Run check-services.ps1 for API probe and final verification."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Windows service update failed" -Details $_.Exception.Message -NextStep "Inspect service state, fix the issue, and rerun update-services.ps1."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
