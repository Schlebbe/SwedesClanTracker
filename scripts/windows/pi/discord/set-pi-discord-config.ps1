[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [string]$DiscordToken,
    [string]$DiscordAdminRoleId,
    [string]$DiscordGuildId,
    [string]$DiscordChannelId,
    [string]$DiscordPetHiscoresChannelId,
    [string[]]$DiscordRankRoleIds = @(),
    [switch]$ClearDiscordRankRoleIds,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\pi-common.ps1")

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

function Convert-RankRoleEntries {
    param([string[]]$Entries)

    $result = [ordered]@{}
    foreach ($entry in @($Entries)) {
        if ([string]::IsNullOrWhiteSpace($entry)) { continue }
        $parts = $entry.Split("=", 2)
        if ($parts.Count -ne 2 -or [string]::IsNullOrWhiteSpace($parts[0]) -or [string]::IsNullOrWhiteSpace($parts[1])) {
            throw "Rank role entry must be formatted as Rank=RoleId. Received: '$entry'"
        }

        $rank = $parts[0].Trim()
        $roleId = $parts[1].Trim()
        Assert-UInt64String -Name "DiscordBot rank role ID for $rank" -Value $roleId
        $result[$rank] = $roleId
    }

    return $result
}

try {
    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

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
    $existingMap = @{}
    foreach ($line in $existingLines) {
        if ($line -match '^\s*#') { continue }
        $parts = $line.Split("=", 2)
        if ($parts.Count -eq 2) {
            $existingMap[$parts[0].Trim()] = $parts[1]
        }
    }

    $existingToken = if ($existingMap.ContainsKey("DiscordBot__Token")) { "$($existingMap["DiscordBot__Token"])" } else { "" }
    $existingAdminRole = if ($existingMap.ContainsKey("DiscordBot__AdminRoleId")) { "$($existingMap["DiscordBot__AdminRoleId"])" } else { "" }
    $existingGuild = if ($existingMap.ContainsKey("DiscordBot__GuildId")) { "$($existingMap["DiscordBot__GuildId"])" } else { "" }
    $existingChannel = if ($existingMap.ContainsKey("DiscordBot__ChannelId")) { "$($existingMap["DiscordBot__ChannelId"])" } else { "" }
    $existingPetHiscores = if ($existingMap.ContainsKey("DiscordBot__PetHiscoresChannelId")) { "$($existingMap["DiscordBot__PetHiscoresChannelId"])" } else { "" }

    if ([string]::IsNullOrWhiteSpace($DiscordToken)) {
        $DiscordToken = if (-not [string]::IsNullOrWhiteSpace($existingToken)) {
            $existingToken.Trim()
        }
        elseif (-not [string]::IsNullOrWhiteSpace($env:DISCORD_BOT_TOKEN)) {
            $env:DISCORD_BOT_TOKEN.Trim()
        }
        else {
            Prompt-NonEmpty -Label "DiscordBot token"
        }
    }
    else {
        $DiscordToken = $DiscordToken.Trim()
    }

    $DiscordAdminRoleId = if ([string]::IsNullOrWhiteSpace($DiscordAdminRoleId)) {
        if (-not [string]::IsNullOrWhiteSpace($existingAdminRole)) { $existingAdminRole.Trim() } else { Prompt-UInt64 -Label "DiscordBot admin role ID" }
    } else { $DiscordAdminRoleId.Trim() }
    $DiscordGuildId = if ([string]::IsNullOrWhiteSpace($DiscordGuildId)) {
        if (-not [string]::IsNullOrWhiteSpace($existingGuild)) { $existingGuild.Trim() } else { Prompt-UInt64 -Label "DiscordBot guild ID" }
    } else { $DiscordGuildId.Trim() }
    $DiscordChannelId = if ([string]::IsNullOrWhiteSpace($DiscordChannelId)) {
        if (-not [string]::IsNullOrWhiteSpace($existingChannel)) { $existingChannel.Trim() } else { Prompt-UInt64 -Label "DiscordBot channel ID" }
    } else { $DiscordChannelId.Trim() }
    $DiscordPetHiscoresChannelId = if ([string]::IsNullOrWhiteSpace($DiscordPetHiscoresChannelId)) {
        if (-not [string]::IsNullOrWhiteSpace($existingPetHiscores)) { $existingPetHiscores.Trim() } else { Prompt-UInt64 -Label "DiscordBot pet hiscores channel ID" }
    } else { $DiscordPetHiscoresChannelId.Trim() }

    if ([string]::IsNullOrWhiteSpace($DiscordToken)) {
        throw "DiscordBot token cannot be empty."
    }

    Assert-UInt64String -Name "DiscordBot admin role ID" -Value $DiscordAdminRoleId
    Assert-UInt64String -Name "DiscordBot guild ID" -Value $DiscordGuildId
    Assert-UInt64String -Name "DiscordBot channel ID" -Value $DiscordChannelId
    Assert-UInt64String -Name "DiscordBot pet hiscores channel ID" -Value $DiscordPetHiscoresChannelId
    $requestedRankRoleIds = Convert-RankRoleEntries -Entries $DiscordRankRoleIds
    $hasRequestedRankRoleIds = $PSBoundParameters.ContainsKey("DiscordRankRoleIds") -or $ClearDiscordRankRoleIds

    $isUnchanged = @(
        $existingToken -eq $DiscordToken
        $existingAdminRole -eq $DiscordAdminRoleId
        $existingGuild -eq $DiscordGuildId
        $existingChannel -eq $DiscordChannelId
        $existingPetHiscores -eq $DiscordPetHiscoresChannelId
        ($existingMap.ContainsKey("DiscordBot__Enabled") -and $existingMap["DiscordBot__Enabled"] -eq "true")
    ) -notcontains $false
    if ($isUnchanged -and $hasRequestedRankRoleIds) {
        foreach ($rank in $requestedRankRoleIds.Keys) {
            $key = "DiscordBot__RankRoleIds__$rank"
            if (-not $existingMap.ContainsKey($key) -or "$($existingMap[$key])" -ne "$($requestedRankRoleIds[$rank])") {
                $isUnchanged = $false
                break
            }
        }
        $existingRankKeys = @($existingMap.Keys | Where-Object { $_ -match '^DiscordBot__RankRoleIds__' })
        if ($existingRankKeys.Count -ne $requestedRankRoleIds.Count) {
            $isUnchanged = $false
        }
    }
    if ($isUnchanged) {
        Write-OpResult -Success $true -Step "Discord configuration unchanged" -Details "Requested Discord settings already active on Pi worker." -NextStep "No restart was needed."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    $updatedLines = @($existingLines | Where-Object {
        $_ -notmatch '^DiscordBot__' -or
        (-not $hasRequestedRankRoleIds -and $_ -match '^DiscordBot__RankRoleIds__')
    })
    $updatedLines += "DiscordBot__Enabled=true"
    $updatedLines += "DiscordBot__Token=$DiscordToken"
    $updatedLines += "DiscordBot__AdminRoleId=$DiscordAdminRoleId"
    $updatedLines += "DiscordBot__GuildId=$DiscordGuildId"
    $updatedLines += "DiscordBot__ChannelId=$DiscordChannelId"
    $updatedLines += "DiscordBot__PetHiscoresChannelId=$DiscordPetHiscoresChannelId"
    if ($hasRequestedRankRoleIds) {
        foreach ($rank in $requestedRankRoleIds.Keys) {
            $updatedLines += "DiscordBot__RankRoleIds__$rank=$($requestedRankRoleIds[$rank])"
        }
    }

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

    $rankRoleDetails = if ($hasRequestedRankRoleIds) { ", RankRoleIds=$($requestedRankRoleIds.Count)" } else { "" }
    $sanitized = "GuildId=$DiscordGuildId, ChannelId=$DiscordChannelId, PetHiscoresChannelId=$DiscordPetHiscoresChannelId, AdminRoleId=$DiscordAdminRoleId$rankRoleDetails"
    Write-OpResult -Success $true -Step "Discord configuration updated" -Details $sanitized -NextStep "Run verify-pi-stack.ps1 to confirm the Pi stack is healthy."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Discord config update failed" -Details $_.Exception.Message -NextStep "Check SSH/sudo access and retry with valid Discord IDs."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
