[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RepoRoot = "",
    [string]$OutputRoot = "",
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

function Invoke-DotnetPublish {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    Write-Host "Publishing $ProjectPath -> $OutputPath"
    New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
    dotnet publish $ProjectPath -c $Configuration -o $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath"
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    }
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = Join-Path $RepoRoot "deploy"
    }

    $apiProject = Join-Path $RepoRoot "SwedesClanTracker.Api\SwedesClanTracker.Api.csproj"
    $workerProject = Join-Path $RepoRoot "SwedesClanTracker.Worker\SwedesClanTracker.Worker.csproj"
    $apiOut = Join-Path $OutputRoot "api"
    $workerOut = Join-Path $OutputRoot "worker"

    Invoke-DotnetPublish -ProjectPath $apiProject -OutputPath $apiOut
    Invoke-DotnetPublish -ProjectPath $workerProject -OutputPath $workerOut

    Write-Host ""
    Write-Host "Publish complete:"
    Write-Host "  API:    $apiOut"
    Write-Host "  Worker: $workerOut"
    Write-OpResult -Success $true -Step "Windows publish complete" -Details "Configuration=$Configuration, OutputRoot=$OutputRoot" -NextStep "Run install-services.ps1 or update-services.ps1 as needed."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Windows publish failed" -Details $_.Exception.Message -NextStep "Install missing SDK/tools and rerun publish-release.ps1."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
