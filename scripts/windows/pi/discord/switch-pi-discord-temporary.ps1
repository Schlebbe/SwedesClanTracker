[CmdletBinding()]
param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

try {
    $scriptPath = Join-Path $PSScriptRoot "set-pi-discord-profile.ps1"
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Missing required script: $scriptPath"
    }

    $scriptArgs = @("-ProfileName", "temporary")
    if ($NoPause) { $scriptArgs += "-NoPause" }

    & $scriptPath @scriptArgs
    exit $LASTEXITCODE
}
catch {
    Write-Host "FAIL: Switch to temporary Discord profile failed"
    Write-Host "Details: $($_.Exception.Message)"
    Write-Host "Next: Check profile files and retry."
    if (-not $NoPause) {
        Read-Host "Done. Press Enter to close"
    }
    exit 1
}
