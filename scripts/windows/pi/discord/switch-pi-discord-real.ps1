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

    $target = "Discord profile 'real'"
    $changeDescription = if ($NoRestart) { "Apply real Discord profile without restarting worker" } else { "Apply real Discord profile and restart worker" }
    if (-not $PSCmdlet.ShouldProcess($target, $changeDescription)) {
        Write-Host "OK: Switch to real Discord profile canceled"
        Write-Host "Details: No changes made."
        if (-not $NoPause) {
            Read-Host "Done. Press Enter to close"
        }
        exit 0
    }

    $scriptParameters = @{ ProfileName = "real"; Confirm = $false }
    if ($NoPause) { $scriptParameters.NoPause = $true }
    if ($NoRestart) { $scriptParameters.NoRestart = $true }

    & $scriptPath @scriptParameters
    exit $LASTEXITCODE
}
catch {
    Write-Host "FAIL: Switch to real Discord profile failed"
    Write-Host "Details: $($_.Exception.Message)"
    Write-Host "Next: Check profile files and retry."
    if (-not $NoPause) {
        Read-Host "Done. Press Enter to close"
    }
    exit 1
}
