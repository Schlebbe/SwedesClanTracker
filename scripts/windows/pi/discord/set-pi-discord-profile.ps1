[CmdletBinding()]
param(
    [ValidateSet("temporary", "real")]
    [string]$ProfileName = "temporary",
    [string]$ProfilesPath = $(Join-Path (Join-Path $PSScriptRoot "..\..\..\..\deploy\env") "discord-profiles.json"),
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [string]$DiscordToken = $env:DISCORD_BOT_TOKEN,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

try {
    if (-not (Test-Path -LiteralPath $ProfilesPath)) {
        $examplePath = Join-Path (Join-Path $PSScriptRoot "..\..\..\..\deploy\env") "discord-profiles.example.json"
        if (Test-Path -LiteralPath $examplePath) {
            throw "Missing profiles file '$ProfilesPath'. Copy '$examplePath' to '$ProfilesPath' and adjust values if needed."
        }
        throw "Missing profiles file: $ProfilesPath"
    }

    $raw = Get-Content -LiteralPath $ProfilesPath -Raw
    $profiles = $raw | ConvertFrom-Json
    $profile = $profiles.$ProfileName
    if ($null -eq $profile) {
        throw "Profile '$ProfileName' not found in $ProfilesPath."
    }

    foreach ($required in @("AdminRoleId", "GuildId", "ChannelId", "PetHiscoresChannelId")) {
        if ([string]::IsNullOrWhiteSpace([string]$profile.$required)) {
            throw "Profile '$ProfileName' is missing required field '$required'."
        }
    }

    $setScript = Join-Path $PSScriptRoot "set-pi-discord-config.ps1"
    if (-not (Test-Path -LiteralPath $setScript)) {
        throw "Missing script: $setScript"
    }

    $args = @(
        "-HostOrIp", $HostOrIp,
        "-User", $User,
        "-KeyPath", $KeyPath,
        "-KnownHostsPath", $KnownHostsPath,
        "-DiscordAdminRoleId", [string]$profile.AdminRoleId,
        "-DiscordGuildId", [string]$profile.GuildId,
        "-DiscordChannelId", [string]$profile.ChannelId,
        "-DiscordPetHiscoresChannelId", [string]$profile.PetHiscoresChannelId
    )
    if ($profile.PSObject.Properties.Name -contains "RankRoleIds") {
        $rankRoleEntries = @()
        if ($null -ne $profile.RankRoleIds) {
            foreach ($roleProperty in $profile.RankRoleIds.PSObject.Properties) {
                if (-not [string]::IsNullOrWhiteSpace([string]$roleProperty.Value)) {
                    $rankRoleEntries += "$($roleProperty.Name)=$($roleProperty.Value)"
                }
            }
        }
        if ($rankRoleEntries.Count -gt 0) {
            $args += @("-DiscordRankRoleIds", $rankRoleEntries)
        }
        else {
            $args += "-ClearDiscordRankRoleIds"
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($DiscordToken)) {
        $args += @("-DiscordToken", $DiscordToken)
    }
    if ($NoPause) { $args += "-NoPause" }

    & $setScript @args
    exit $LASTEXITCODE
}
catch {
    Write-Host "FAIL: Discord profile switch failed"
    Write-Host "Details: $($_.Exception.Message)"
    Write-Host "Next: Verify profiles file and retry."
    if (-not $NoPause) {
        Read-Host "Done. Press Enter to close"
    }
    exit 1
}
