[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [string]$RepoRoot = "",
    [switch]$SkipBuild,
    [switch]$SkipVerify,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$WorkingDirectory = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        Push-Location $WorkingDirectory
    }
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Pop-Location
        }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
    }

    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"

    $outputRoot = Join-Path $RepoRoot "deploy\pi"
    $frontendDir = Join-Path $RepoRoot "swedesclantracker-frontend"
    $frontendOut = Join-Path $outputRoot "frontend"

    if (-not $PSCmdlet.ShouldProcess("$User@$HostOrIp", "Deploy Pi frontend only")) {
        Write-OpResult -Success $true -Step "Pi frontend deploy canceled" -Details "No deployment changes were applied." -NextStep "Run script again and confirm to deploy frontend."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    if (-not $SkipBuild) {
        if (Test-Path -LiteralPath $frontendOut) { Remove-Item -LiteralPath $frontendOut -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $frontendOut | Out-Null

        if (Test-Path -LiteralPath (Join-Path $frontendDir "package-lock.json")) {
            Invoke-CheckedCommand -FilePath "npm" -Arguments @("ci") -WorkingDirectory $frontendDir
        }
        else {
            Invoke-CheckedCommand -FilePath "npm" -Arguments @("install") -WorkingDirectory $frontendDir
        }
        Invoke-CheckedCommand -FilePath "npm" -Arguments @("run", "build") -WorkingDirectory $frontendDir

        Copy-Item -Path (Join-Path $frontendDir "dist\*") -Destination $frontendOut -Recurse -Force
        Write-OpResult -Success $true -Step "Frontend build complete" -Details "Artifacts prepared in $frontendOut"
    }
    else {
        Write-OpResult -Success $true -Step "Frontend build skipped" -Details "Using existing artifacts in $frontendOut"
    }

    if (-not (Test-Path -LiteralPath $frontendOut)) {
        throw "Missing frontend artifact directory: $frontendOut"
    }

    $prepRemote = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand "rm -rf /tmp/swedesclantracker-frontend-upload && mkdir -p /tmp/swedesclantracker-frontend-upload"
    if ($prepRemote.ExitCode -ne 0) {
        throw "Failed to prepare remote frontend upload directory."
    }

    $copyFrontend = Invoke-ScpUpload -LocalPath $frontendOut -RemotePath "/tmp/swedesclantracker-frontend-upload/" -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -Recurse
    if ($copyFrontend.ExitCode -ne 0) { throw "Failed to upload frontend artifacts." }

    $deployCommand = @"
sudo -n mkdir -p /opt/swedesclantracker/frontend
sudo -n rsync -a --delete /tmp/swedesclantracker-frontend-upload/frontend/ /opt/swedesclantracker/frontend/
sudo -n chown -R swedestracker:swedestracker /opt/swedesclantracker/frontend
sudo -n find /opt/swedesclantracker/frontend -type d -exec chmod 755 {} +
sudo -n find /opt/swedesclantracker/frontend -type f -exec chmod 644 {} +
sudo -n systemctl reload nginx
"@
    if (-not $SkipVerify) {
        $deployCommand += "`nsudo -n systemctl is-active nginx"
    }

    $deployResult = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $deployCommand
    if ($deployResult.Output) {
        $deployResult.Output | Out-Host
    }
    if ($deployResult.ExitCode -ne 0) {
        throw "Pi frontend deployment command failed."
    }

    $details = if ($SkipVerify) { "Frontend artifacts were deployed and nginx was reloaded." } else { "Frontend artifacts were deployed, nginx reloaded, and nginx is active." }
    Write-OpResult -Success $true -Step "Pi frontend deployment completed" -Details $details -NextStep "Hard refresh the dashboard in browser and verify expected UI changes."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Frontend deploy workflow failed" -Details $_.Exception.Message -NextStep "Resolve the failing step and rerun deploy-pi-frontend.ps1."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
