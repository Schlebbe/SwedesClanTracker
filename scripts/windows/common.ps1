Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-PathWithPrompt {
    param(
        [string]$PathValue,
        [string]$PromptLabel
    )

    if ([string]::IsNullOrWhiteSpace($PathValue) -or !(Test-Path -LiteralPath $PathValue)) {
        $inputPath = Read-Host "$PromptLabel [$PathValue]"
        if (-not [string]::IsNullOrWhiteSpace($inputPath)) {
            $PathValue = $inputPath.Trim()
        }
    }

    if ([string]::IsNullOrWhiteSpace($PathValue) -or !(Test-Path -LiteralPath $PathValue)) {
        throw "Missing required path: $PathValue"
    }

    return $PathValue
}

function Write-OpResult {
    param(
        [Parameter(Mandatory = $true)][bool]$Success,
        [Parameter(Mandatory = $true)][string]$Step,
        [string]$Details = "",
        [string]$NextStep = ""
    )

    $status = if ($Success) { "OK" } else { "FAIL" }
    Write-Host "${status}: $Step"
    if (-not [string]::IsNullOrWhiteSpace($Details)) {
        Write-Host "Details: $Details"
    }
    if (-not [string]::IsNullOrWhiteSpace($NextStep)) {
        Write-Host "Next: $NextStep"
    }
}

function Prompt-UInt64 {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$DefaultValue = ""
    )

    while ($true) {
        $prompt = if ([string]::IsNullOrWhiteSpace($DefaultValue)) { $Label } else { "$Label [$DefaultValue]" }
        $value = Read-Host $prompt
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = $DefaultValue
        }
        $parsed = 0
        if ([UInt64]::TryParse($value, [ref]$parsed)) {
            return $parsed.ToString()
        }
        Write-Host "Please enter a numeric value." -ForegroundColor Yellow
    }
}

function Prompt-NonEmpty {
    param(
        [Parameter(Mandatory = $true)][string]$Label
    )

    while ($true) {
        $value = Read-Host $Label
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim()
        }
        Write-Host "Value cannot be empty." -ForegroundColor Yellow
    }
}

function Read-ServiceAction {
    param(
        [string]$DefaultAction = "status"
    )

    $valid = @("start", "stop", "restart", "status")
    while ($true) {
        $value = Read-Host "Action (start/stop/restart/status) [$DefaultAction]"
        if ([string]::IsNullOrWhiteSpace($value)) {
            return $DefaultAction
        }
        $normalized = $value.Trim().ToLowerInvariant()
        if ($valid -contains $normalized) {
            return $normalized
        }
        Write-Host "Invalid action." -ForegroundColor Yellow
    }
}

function Pause-IfRequested {
    param([switch]$NoPause)
    if (-not $NoPause) {
        Read-Host "Done. Press Enter to close"
    }
}
