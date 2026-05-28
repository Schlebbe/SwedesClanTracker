[CmdletBinding()]
param(
    [string]$ProfilesPath = $(Join-Path (Join-Path $PSScriptRoot "..\..\..\..\deploy\env") "discord-profiles.json"),
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\pi-common.ps1")

try {
    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    $readEnv = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand "sudo -n cat /etc/swedesclantracker/worker.env"
    if ($readEnv.ExitCode -ne 0) {
        throw "Failed to read /etc/swedesclantracker/worker.env"
    }

    $map = @{}
    foreach ($line in @($readEnv.Output | ForEach-Object { "$_" })) {
        if ($line -match '^\s*#') { continue }
        $parts = $line.Split("=", 2)
        if ($parts.Count -eq 2) { $map[$parts[0].Trim()] = $parts[1] }
    }

    $current = @{
        AdminRoleId = if ($map.ContainsKey("DiscordBot__AdminRoleId")) { "$($map["DiscordBot__AdminRoleId"])" } else { "" }
        GuildId = if ($map.ContainsKey("DiscordBot__GuildId")) { "$($map["DiscordBot__GuildId"])" } else { "" }
        ChannelId = if ($map.ContainsKey("DiscordBot__ChannelId")) { "$($map["DiscordBot__ChannelId"])" } else { "" }
        PetHiscoresChannelId = if ($map.ContainsKey("DiscordBot__PetHiscoresChannelId")) { "$($map["DiscordBot__PetHiscoresChannelId"])" } else { "" }
    }
    $tokenSet = $map.ContainsKey("DiscordBot__Token") -and -not [string]::IsNullOrWhiteSpace("$($map["DiscordBot__Token"])")

    if (-not (Test-Path -LiteralPath $ProfilesPath)) {
        throw "Missing profiles file: $ProfilesPath"
    }
    $profiles = (Get-Content -LiteralPath $ProfilesPath -Raw | ConvertFrom-Json)

    $matchedProfile = "custom"
    foreach ($candidateName in @("temporary", "real")) {
        $candidate = $profiles.$candidateName
        if ($null -eq $candidate) { continue }
        if ("$($candidate.AdminRoleId)" -eq $current.AdminRoleId -and
            "$($candidate.GuildId)" -eq $current.GuildId -and
            "$($candidate.ChannelId)" -eq $current.ChannelId -and
            "$($candidate.PetHiscoresChannelId)" -eq $current.PetHiscoresChannelId) {
            $matchedProfile = $candidateName
            break
        }
    }

    Write-Host "OK: Current Pi Discord worker profile"
    Write-Host "Profile: $matchedProfile"
    Write-Host "GuildId: $($current.GuildId)"
    Write-Host "ChannelId: $($current.ChannelId)"
    Write-Host "PetHiscoresChannelId: $($current.PetHiscoresChannelId)"
    Write-Host "AdminRoleId: $($current.AdminRoleId)"
    Write-Host "TokenSet: $tokenSet"
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-Host "FAIL: Discord profile check failed"
    Write-Host "Details: $($_.Exception.Message)"
    Write-Host "Next: Verify SSH/sudo and profiles file path, then retry."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
