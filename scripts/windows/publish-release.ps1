[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RepoRoot = "",
    [string]$OutputRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot "deploy"
}

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
