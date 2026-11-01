[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$NoPause,
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"

try {
    $scriptPath = Join-Path $PSScriptRoot "set-pi-discord-profile.ps1"
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Missing required script: $scriptPath"
    }

    $target = "Discord profile 'temporary'"
    $changeDescription = if ($NoRestart) { "Apply temporary Discord profile without restarting worker" } else { "Apply temporary Discord profile and restart worker" }
    if (-not $PSCmdlet.ShouldProcess($target, $changeDescription)) {
        Write-Host "OK: Switch to temporary Discord profile canceled"
        Write-Host "Details: No changes made."
        if (-not $NoPause) {
            Read-Host "Done. Press Enter to close"
        }
        exit 0
    }

    $scriptParameters = @{ ProfileName = "temporary"; Confirm = $false }
    if ($NoPause) { $scriptParameters.NoPause = $true }
    if ($NoRestart) { $scriptParameters.NoRestart = $true }

    & $scriptPath @scriptParameters
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
