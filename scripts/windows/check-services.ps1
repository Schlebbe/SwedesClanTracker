[CmdletBinding()]
param(
    [string]$ApiServiceName = "SwedesClanTracker-Api",
    [string]$WorkerServiceName = "SwedesClanTracker-Worker",
    [string]$ApiHealthUrl = "http://127.0.0.1:5166/api/dashboard"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Show-ServiceState {
    param([Parameter(Mandatory = $true)][string]$Name)
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host "${Name}: NOT INSTALLED"
        return
    }
    $wmi = Get-CimInstance Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
    $startMode = if ($null -eq $wmi) { "Unknown" } else { $wmi.StartMode }
    Write-Host "${Name}: $($svc.Status) (StartMode=$startMode)"
}

Show-ServiceState -Name $ApiServiceName
Show-ServiceState -Name $WorkerServiceName

Write-Host ""
Write-Host "API probe (expects 401 when unauthenticated): $ApiHealthUrl"
try {
    $response = Invoke-WebRequest -Uri $ApiHealthUrl -Method Get -UseBasicParsing -TimeoutSec 10
    Write-Host "API response: $($response.StatusCode)"
}
catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
        $status = [int]$_.Exception.Response.StatusCode
        Write-Host "API response: $status"
    }
    else {
        Write-Host "API probe failed: $($_.Exception.Message)"
    }
}
