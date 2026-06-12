[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$ApiServiceName = "SwedesClanTracker-Api",
    [string]$WorkerServiceName = "SwedesClanTracker-Worker",
    [switch]$NoPause,
    [switch]$ElevatedRelaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

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

function Invoke-Sc {
    param([Parameter(Mandatory = $true)][string[]]$Args)
    & sc.exe @Args | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failed: $($Args -join ' ')"
    }
}

function Remove-ServiceIfExists {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Write-Host "Service not found, skipping: $Name"
        return
    }

    if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
        Stop-Service -Name $Name -Force
    }

    Invoke-Sc -Args @("delete", $Name)
    Write-Host "Removed service: $Name"
}

try {
    Ensure-ElevatedOrRelaunch

    if (-not $PSCmdlet.ShouldProcess("$ApiServiceName, $WorkerServiceName", "Uninstall Windows services")) {
        Write-OpResult -Success $true -Step "Service uninstall canceled" -Details "No local service changes were made." -NextStep "Rerun and confirm when ready."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }
    Assert-Admin

    Remove-ServiceIfExists -Name $ApiServiceName
    Remove-ServiceIfExists -Name $WorkerServiceName
    Write-OpResult -Success $true -Step "Windows service uninstall completed" -Details "ApiService=$ApiServiceName, WorkerService=$WorkerServiceName" -NextStep "Run check-services.ps1 to confirm they are removed."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Windows service uninstall failed" -Details $_.Exception.Message -NextStep "Resolve the issue and rerun uninstall-services.ps1."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
