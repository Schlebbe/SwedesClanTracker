[CmdletBinding()]
param(
    [string]$ApiServiceName = "SwedesClanTracker-Api",
    [string]$WorkerServiceName = "SwedesClanTracker-Worker"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script in an elevated PowerShell session (Run as Administrator)."
    }
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

Assert-Admin
Remove-ServiceIfExists -Name $ApiServiceName
Remove-ServiceIfExists -Name $WorkerServiceName
