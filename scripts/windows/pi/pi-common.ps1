Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\common.ps1")

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
