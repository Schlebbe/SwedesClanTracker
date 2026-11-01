[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [string]$Configuration = "Release",
    [string]$Runtime = "linux-arm64",
    [string]$RepoRoot = "",
    [switch]$SkipPublish,
    [switch]$SkipVerify,
    [switch]$NoPause,
    [switch]$NoRestart,
    [switch]$BackendOnly
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

function Assert-DeploymentOutputPath {
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$CandidatePath
    )

    $resolvedRoot = [IO.Path]::GetFullPath($OutputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedCandidate = [IO.Path]::GetFullPath($CandidatePath)
    $rootPrefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
    if ([string]::Equals($resolvedCandidate, $resolvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedCandidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove deployment output outside outputRoot: $resolvedCandidate"
    }
}

function New-Base64RemoteCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Script
    )

    $normalizedScript = $Script.Replace("`r`n", "`n").Replace("`r", "`n")
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($normalizedScript))
    return "printf '%s' '$payload' | base64 -d | bash"
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
    $apiProject = Join-Path $RepoRoot "SwedesClanTracker.Api\SwedesClanTracker.Api.csproj"
    $workerProject = Join-Path $RepoRoot "SwedesClanTracker.Worker\SwedesClanTracker.Worker.csproj"
    $frontendDir = Join-Path $RepoRoot "swedesclantracker-frontend"

    if (-not $PSCmdlet.ShouldProcess("$User@$HostOrIp", "Deploy Pi stack")) {
        Write-OpResult -Success $true -Step "Pi deploy canceled" -Details "No deployment changes were applied." -NextStep "Run script again and confirm to deploy."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }

    if (-not $SkipPublish) {
        $apiOut = Join-Path $outputRoot "api"
        $workerOut = Join-Path $outputRoot "worker"
        $frontendOut = Join-Path $outputRoot "frontend"

        $publishOutputs = @($apiOut, $workerOut)
        if (-not $BackendOnly) {
            $publishOutputs += $frontendOut
        }
        foreach ($publishOutput in $publishOutputs) {
            Assert-DeploymentOutputPath -OutputRoot $outputRoot -CandidatePath $publishOutput
            if (Test-Path -LiteralPath $publishOutput) {
                Remove-Item -LiteralPath $publishOutput -Recurse -Force
            }
        }

        New-Item -ItemType Directory -Force -Path $publishOutputs | Out-Null

        Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("publish", $apiProject, "--configuration", $Configuration, "--runtime", $Runtime, "--self-contained", "false", "--output", $apiOut)
        Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("publish", $workerProject, "--configuration", $Configuration, "--runtime", $Runtime, "--self-contained", "false", "--output", $workerOut)

        if (-not $BackendOnly) {
            if (Test-Path -LiteralPath (Join-Path $frontendDir "package-lock.json")) {
                Invoke-CheckedCommand -FilePath "npm" -Arguments @("ci") -WorkingDirectory $frontendDir
            }
            else {
                Invoke-CheckedCommand -FilePath "npm" -Arguments @("install") -WorkingDirectory $frontendDir
            }
            Invoke-CheckedCommand -FilePath "npm" -Arguments @("run", "build") -WorkingDirectory $frontendDir

            Copy-Item -Path (Join-Path $frontendDir "dist\*") -Destination $frontendOut -Recurse -Force
        }
        $publishDetails = if ($BackendOnly) { "API and Worker artifacts prepared in $outputRoot; frontend was left untouched." } else { "API, Worker, and frontend artifacts prepared in $outputRoot." }
        Write-OpResult -Success $true -Step "Local publish complete" -Details $publishDetails
    }
    else {
        Write-OpResult -Success $true -Step "Local publish skipped" -Details "Using existing artifacts in $outputRoot"
    }

    $apiDir = Join-Path $outputRoot "api"
    $workerDir = Join-Path $outputRoot "worker"
    $frontendDirOut = Join-Path $outputRoot "frontend"
    $artifactDirectories = @($apiDir, $workerDir)
    if (-not $BackendOnly) {
        $artifactDirectories += $frontendDirOut
    }
    foreach ($path in $artifactDirectories) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Missing deployment artifact directory: $path"
        }
    }

    $stoppedCheckFunction = @'
check_services_stopped() {
    for service in swedesclantracker-api swedesclantracker-worker; do
        state="$(sudo -n systemctl show "$service" --property=ActiveState --value)" || {
            echo "Unable to inspect $service" >&2
            return 1
        }
        echo "$service: $state"
        if [ "$state" != "inactive" ]; then
            echo "$service must be inactive during a no-restart deployment (observed: $state)" >&2
            return 1
        fi
    done
}
'@
    if ($NoRestart) {
        $stoppedCheckScript = "set -eu`n" + $stoppedCheckFunction.TrimEnd() + "`ncheck_services_stopped`n"
        $stoppedBefore = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand (New-Base64RemoteCommand -Script $stoppedCheckScript)
        if ($stoppedBefore.Output) {
            $stoppedBefore.Output | Out-Host
        }
        if ($stoppedBefore.ExitCode -ne 0) {
            throw "NoRestart requires both API and Worker services to be inactive before installation."
        }
    }

    $prepRemote = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand "rm -rf /tmp/swedesclantracker-upload && mkdir -p /tmp/swedesclantracker-upload"
    if ($prepRemote.ExitCode -ne 0) {
        throw "Failed to prepare remote upload directory."
    }

    $copyApi = Invoke-ScpUpload -LocalPath $apiDir -RemotePath "/tmp/swedesclantracker-upload/" -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -Recurse
    if ($copyApi.ExitCode -ne 0) { throw "Failed to upload API artifacts." }
    $copyWorker = Invoke-ScpUpload -LocalPath $workerDir -RemotePath "/tmp/swedesclantracker-upload/" -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -Recurse
    if ($copyWorker.ExitCode -ne 0) { throw "Failed to upload worker artifacts." }
    if (-not $BackendOnly) {
        $copyFrontend = Invoke-ScpUpload -LocalPath $frontendDirOut -RemotePath "/tmp/swedesclantracker-upload/" -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -Recurse
        if ($copyFrontend.ExitCode -ne 0) { throw "Failed to upload frontend artifacts." }
    }

    $installLines = @(
        "sudo -n mkdir -p /opt/swedesclantracker/api /opt/swedesclantracker/worker",
        "sudo -n rsync -a --delete /tmp/swedesclantracker-upload/api/ /opt/swedesclantracker/api/",
        "sudo -n rsync -a --delete /tmp/swedesclantracker-upload/worker/ /opt/swedesclantracker/worker/"
    )
    if (-not $BackendOnly) {
        $installLines += @(
            "sudo -n mkdir -p /opt/swedesclantracker/frontend",
            "sudo -n rsync -a --delete /tmp/swedesclantracker-upload/frontend/ /opt/swedesclantracker/frontend/"
        )
    }
    $ownershipPath = if ($BackendOnly) {
        "/opt/swedesclantracker/api /opt/swedesclantracker/worker"
    }
    else {
        "/opt/swedesclantracker"
    }
    $installLines += "sudo -n chown -R swedestracker:swedestracker $ownershipPath"
    if (-not $BackendOnly) {
        $installLines += @(
            "sudo -n find /opt/swedesclantracker/frontend -type d -exec chmod 755 {} +",
            "sudo -n find /opt/swedesclantracker/frontend -type f -exec chmod 644 {} +"
        )
    }
    $installCommand = $installLines -join "`n"

    if ($NoRestart) {
        $deployScript = "set -eu`n" +
            $stoppedCheckFunction.TrimEnd() + "`n" +
            "check_services_stopped`n" +
            $installCommand + "`n" +
            "check_services_stopped`n"
        $deployCommand = New-Base64RemoteCommand -Script $deployScript
    }
    else {
        $deployCommand = @"
set -e
sudo -n systemctl stop swedesclantracker-api swedesclantracker-worker
$installCommand
sudo -n systemctl start swedesclantracker-api swedesclantracker-worker
sudo -n systemctl reload nginx
sudo -n systemctl is-active swedesclantracker-api swedesclantracker-worker nginx
"@
    }
    $deployResult = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $deployCommand
    if ($deployResult.Output) {
        $deployResult.Output | Out-Host
    }
    if ($deployResult.ExitCode -ne 0) {
        throw "Pi deployment command failed."
    }

    if ($NoRestart) {
        Write-OpResult -Success $true -Step "Pi deployment completed" -Details "Artifacts were installed while API and Worker remained stopped; no services were started or reloaded." -NextStep "Perform the manual cutover checks, then start services deliberately and run verification."
    }
    else {
        Write-OpResult -Success $true -Step "Pi deployment completed" -Details "Artifacts were deployed and services restarted." -NextStep "Run verification to confirm stack health."
    }

    if (-not $NoRestart -and -not $SkipVerify) {
        $verifyScript = Join-Path $PSScriptRoot "verify-pi-stack.ps1"
        & $verifyScript -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -NoPause
        if ($LASTEXITCODE -ne 0) {
            Write-OpResult -Success $false -Step "Post-deploy verification failed" -Details "verify-pi-stack.ps1 returned a failure." -NextStep "Fix issues reported by verification, then rerun deploy or verify."
            Pause-IfRequested -NoPause:$NoPause
            exit 1
        }
    }

    if ($NoRestart) {
        Write-OpResult -Success $true -Step "Deploy workflow finished" -Details "Deployment finished with API and Worker paused; automatic health verification was skipped." -NextStep "Complete the reviewed cutover, then start services and run verify-pi-stack.ps1."
    }
    else {
        Write-OpResult -Success $true -Step "Deploy workflow finished" -Details "Pi stack is deployed." -NextStep "Proceed with burn-in checks and Discord validation."
    }
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Deploy workflow failed" -Details $_.Exception.Message -NextStep "Resolve the failing step and rerun deploy-pi-stack.ps1."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
