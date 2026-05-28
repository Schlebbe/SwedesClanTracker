Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-PiHost {
    param(
        [string]$HostOrIp = $env:PI_HOST_OR_IP
    )

    if ([string]::IsNullOrWhiteSpace($HostOrIp)) {
        $defaultHost = "192.168.10.106"
        $inputHost = Read-Host "Pi host/IP [$defaultHost]"
        if ([string]::IsNullOrWhiteSpace($inputHost)) {
            $HostOrIp = $defaultHost
        }
        else {
            $HostOrIp = $inputHost.Trim()
        }
    }

    return $HostOrIp
}

function Resolve-PiUser {
    param(
        [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" })
    )

    if ([string]::IsNullOrWhiteSpace($User)) {
        $inputUser = Read-Host "Pi SSH user [sebastian]"
        $User = if ([string]::IsNullOrWhiteSpace($inputUser)) { "sebastian" } else { $inputUser.Trim() }
    }

    return $User
}

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

function New-SshArgs {
    param(
        [Parameter(Mandatory = $true)][string]$HostOrIp,
        [Parameter(Mandatory = $true)][string]$User,
        [Parameter(Mandatory = $true)][string]$KeyPath,
        [Parameter(Mandatory = $true)][string]$KnownHostsPath,
        [Parameter(Mandatory = $true)][string]$RemoteCommand
    )

    return @(
        "-i", $KeyPath,
        "-o", "UserKnownHostsFile=$KnownHostsPath",
        "-o", "StrictHostKeyChecking=yes",
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=8",
        "$User@$HostOrIp",
        $RemoteCommand
    )
}

function Invoke-Ssh {
    param(
        [Parameter(Mandatory = $true)][string]$HostOrIp,
        [Parameter(Mandatory = $true)][string]$User,
        [Parameter(Mandatory = $true)][string]$KeyPath,
        [Parameter(Mandatory = $true)][string]$KnownHostsPath,
        [Parameter(Mandatory = $true)][string]$RemoteCommand
    )

    # Normalize CRLF to LF so Linux shells do not receive stray '\r' characters.
    $normalizedCommand = $RemoteCommand -replace "`r`n", "`n"
    $normalizedCommand = $normalizedCommand -replace "`r", "`n"
    $sshArgs = New-SshArgs -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $normalizedCommand
    $output = & ssh @sshArgs 2>&1
    $exitCode = $LASTEXITCODE
    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-ScpUpload {
    param(
        [Parameter(Mandatory = $true)][string]$LocalPath,
        [Parameter(Mandatory = $true)][string]$RemotePath,
        [Parameter(Mandatory = $true)][string]$HostOrIp,
        [Parameter(Mandatory = $true)][string]$User,
        [Parameter(Mandatory = $true)][string]$KeyPath,
        [Parameter(Mandatory = $true)][string]$KnownHostsPath,
        [switch]$Recurse
    )

    $scpArgs = @(
        "-i", $KeyPath,
        "-o", "UserKnownHostsFile=$KnownHostsPath",
        "-o", "StrictHostKeyChecking=yes",
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=8"
    )
    if ($Recurse) {
        $scpArgs = @("-r") + $scpArgs
    }
    $scpArgs += @(
        $LocalPath,
        "${User}@${HostOrIp}:$RemotePath"
    )

    $output = & scp @scpArgs 2>&1
    $exitCode = $LASTEXITCODE
    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = $output
    }
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
