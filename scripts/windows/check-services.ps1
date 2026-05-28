[CmdletBinding()]
param(
    [string]$ApiServiceName = "SwedesClanTracker-Api",
    [string]$WorkerServiceName = "SwedesClanTracker-Worker",
    [string]$ApiHealthUrl = "http://127.0.0.1:5166/api/dashboard",
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

function Show-ServiceState {
    param([Parameter(Mandatory = $true)][string]$Name)
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host "${Name}: NOT INSTALLED"
        return "$Name=NotInstalled"
    }
    $wmi = Get-CimInstance Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
    $startMode = if ($null -eq $wmi) { "Unknown" } else { $wmi.StartMode }
    Write-Host "${Name}: $($svc.Status) (StartMode=$startMode)"
    return "$Name=$($svc.Status)"
}

try {
    $serviceStates = @()
    $serviceStates += Show-ServiceState -Name $ApiServiceName
    $serviceStates += Show-ServiceState -Name $WorkerServiceName

    Write-Host ""
    Write-Host "API probe (expects 401 when unauthenticated): $ApiHealthUrl"
    $apiStatusText = "Unknown"
    try {
        $response = Invoke-WebRequest -Uri $ApiHealthUrl -Method Get -UseBasicParsing -TimeoutSec 10
        $apiStatusText = "$($response.StatusCode)"
        Write-Host "API response: $apiStatusText"
    }
    catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $status = [int]$_.Exception.Response.StatusCode
            $apiStatusText = "$status"
            Write-Host "API response: $status"
        }
        else {
            $apiStatusText = "ProbeFailed"
            Write-Host "API probe failed: $($_.Exception.Message)"
        }
    }

    $details = (($serviceStates | Where-Object { $_ }) -join ", ") + ", ApiProbe=$apiStatusText"
    Write-OpResult -Success $true -Step "Windows service check complete" -Details $details -NextStep "If any service is stopped, run update-services.ps1 or start services manually."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Windows service check failed" -Details $_.Exception.Message -NextStep "Fix the failing step and rerun check-services.ps1."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
