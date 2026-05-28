[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { Join-Path $HOME ".ssh\id_ed25519" }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { Join-Path $HOME ".ssh\known_hosts" }),
    [string]$DiscordToken,
    [string]$DiscordAdminRoleId,
    [string]$DiscordGuildId,
    [string]$DiscordChannelId,
    [string]$DiscordPetHiscoresChannelId,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

function Assert-UInt64String {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $parsed = [UInt64]0
    if (-not [UInt64]::TryParse($Value, [ref]$parsed)) {
        throw "$Name must be numeric. Received: '$Value'"
    }
}

try {
    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    $DiscordToken = if ([string]::IsNullOrWhiteSpace($DiscordToken)) { Prompt-NonEmpty -Label "DiscordBot token" } else { $DiscordToken.Trim() }
    $DiscordAdminRoleId = if ([string]::IsNullOrWhiteSpace($DiscordAdminRoleId)) { Prompt-UInt64 -Label "DiscordBot admin role ID" } else { $DiscordAdminRoleId.Trim() }
    $DiscordGuildId = if ([string]::IsNullOrWhiteSpace($DiscordGuildId)) { Prompt-UInt64 -Label "DiscordBot guild ID" } else { $DiscordGuildId.Trim() }
    $DiscordChannelId = if ([string]::IsNullOrWhiteSpace($DiscordChannelId)) { Prompt-UInt64 -Label "DiscordBot channel ID" } else { $DiscordChannelId.Trim() }
    $DiscordPetHiscoresChannelId = if ([string]::IsNullOrWhiteSpace($DiscordPetHiscoresChannelId)) { Prompt-UInt64 -Label "DiscordBot pet hiscores channel ID" } else { $DiscordPetHiscoresChannelId.Trim() }

    if ([string]::IsNullOrWhiteSpace($DiscordToken)) {
        throw "DiscordBot token cannot be empty."
    }

    Assert-UInt64String -Name "DiscordBot admin role ID" -Value $DiscordAdminRoleId
    Assert-UInt64String -Name "DiscordBot guild ID" -Value $DiscordGuildId
    Assert-UInt64String -Name "DiscordBot channel ID" -Value $DiscordChannelId
    Assert-UInt64String -Name "DiscordBot pet hiscores channel ID" -Value $DiscordPetHiscoresChannelId

    $target = "/etc/swedesclantracker/worker.env on $User@$HostOrIp"
    if (-not $PSCmdlet.ShouldProcess($target, "Update DiscordBot configuration and restart worker")) {
        Write-OpResult -Success $true -Step "Discord config update canceled" -Details "No changes made." -NextStep "Run again and confirm to apply new Discord values."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    $readEnv = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand "sudo -n cat /etc/swedesclantracker/worker.env"
    if ($readEnv.ExitCode -ne 0) {
        throw "Failed to read /etc/swedesclantracker/worker.env"
    }

    $existingLines = @($readEnv.Output | ForEach-Object { "$_" })
    $updatedLines = @($existingLines | Where-Object { $_ -notmatch '^DiscordBot__' })
    $updatedLines += "DiscordBot__Enabled=true"
    $updatedLines += "DiscordBot__Token=$DiscordToken"
    $updatedLines += "DiscordBot__AdminRoleId=$DiscordAdminRoleId"
    $updatedLines += "DiscordBot__GuildId=$DiscordGuildId"
    $updatedLines += "DiscordBot__ChannelId=$DiscordChannelId"
    $updatedLines += "DiscordBot__PetHiscoresChannelId=$DiscordPetHiscoresChannelId"

    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllLines($tempFile, $updatedLines, $utf8NoBom)

        $remoteTemp = "/tmp/sct-worker-env-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()).tmp"
        $copyResult = Invoke-ScpUpload -LocalPath $tempFile -RemotePath $remoteTemp -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath
        if ($copyResult.ExitCode -ne 0) {
            throw "Failed to upload updated worker.env to Pi staging path."
        }

        $applyCommand = @"
sudo -n systemctl stop swedesclantracker-worker
sudo -n install -m 0600 '$remoteTemp' /etc/swedesclantracker/worker.env
sudo -n rm -f '$remoteTemp'
sudo -n systemctl start swedesclantracker-worker
sudo -n systemctl is-active swedesclantracker-worker
"@
        $applyResult = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $applyCommand
        if ($applyResult.Output) {
            $applyResult.Output | Out-Host
        }
        if ($applyResult.ExitCode -ne 0) {
            throw "Failed to apply new worker.env and restart worker service."
        }
    }
    finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }

    $sanitized = "GuildId=$DiscordGuildId, ChannelId=$DiscordChannelId, PetHiscoresChannelId=$DiscordPetHiscoresChannelId, AdminRoleId=$DiscordAdminRoleId"
    Write-OpResult -Success $true -Step "Discord configuration updated" -Details $sanitized -NextStep "Run verify-pi-stack.ps1 to confirm the Pi stack is healthy."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Discord config update failed" -Details $_.Exception.Message -NextStep "Check SSH/sudo access and retry with valid Discord IDs."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
