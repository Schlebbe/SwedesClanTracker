[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$RepoRoot = "",
    [string]$PublishRoot = "",
    [string]$ApiServiceName = "SwedesClanTracker-Api",
    [string]$WorkerServiceName = "SwedesClanTracker-Worker",
    [switch]$PublishFirst,
    [switch]$StartAfterInstall = $true,
    [switch]$UseLocalSystem = $true,
    [System.Management.Automation.PSCredential]$ServiceCredential = [System.Management.Automation.PSCredential]::Empty,
    [string]$ServiceAccount,
    [string]$ServicePassword,
    [string]$ConnectionString,
    [string]$TempleApiKey,
    [string]$WiseOldManVerificationCode,
    [string]$DiscordBotToken,
    [string]$DiscordAdminRoleId,
    [string]$AuthUsername,
    [string]$AuthPassword,
    [switch]$NoPause,
    [switch]$ElevatedRelaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script in an elevated PowerShell session (Run as Administrator)."
    }
}

function Ensure-ElevatedOrRelaunch {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        return
    }
    if ($ElevatedRelaunch) {
        throw "Script requires Administrator rights."
    }

    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-NoPause", "-ElevatedRelaunch")
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        if ($entry.Key -eq "NoPause" -or $entry.Key -eq "ElevatedRelaunch") {
            continue
        }
        if ($entry.Value -is [System.Management.Automation.SwitchParameter]) {
            if (-not $entry.Value.IsPresent) {
                continue
            }
            $argList += "-$($entry.Key)"
            continue
        }
        $argList += "-$($entry.Key)"
        $argList += "$($entry.Value)"
    }

    $proc = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $argList -Wait -PassThru
    $proc.WaitForExit()
    exit $proc.ExitCode
}

function Invoke-Sc {
    param([Parameter(Mandatory = $true)][string[]]$Args)
    & sc.exe @Args | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failed: $($Args -join ' ')"
    }
}

function Set-ServiceRecovery {
    param([Parameter(Mandatory = $true)][string]$Name)
    Invoke-Sc -Args @("failure", $Name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000")
    Invoke-Sc -Args @("failureflag", $Name, "1")
}

function Convert-SecureStringToPlainText {
    param([Parameter(Mandatory = $true)][Security.SecureString]$SecureString)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Set-ServiceEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Values
    )

    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if ($Values.Count -eq 0) {
        Remove-ItemProperty -Path $regPath -Name "Environment" -ErrorAction SilentlyContinue
        return
    }

    $existing = Get-ItemProperty -Path $regPath -Name "Environment" -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        New-ItemProperty -Path $regPath -Name "Environment" -PropertyType MultiString -Value $Values | Out-Null
    }
    else {
        Set-ItemProperty -Path $regPath -Name "Environment" -Value $Values
    }
}

function Install-Or-UpdateService {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string[]]$EnvironmentVariables,
        [Parameter(Mandatory = $true)][bool]$UseLocalSystem,
        [string]$RunAsAccount,
        [string]$RunAsPassword
    )

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    $binPath = "`"$ExecutablePath`""

    $baseCreateOrConfigArgs = @("binPath=", $binPath, "start=", "auto", "displayname=", $DisplayName)
    if ($UseLocalSystem) {
        $baseCreateOrConfigArgs += @("obj=", "LocalSystem")
    }
    elseif (-not [string]::IsNullOrWhiteSpace($RunAsAccount)) {
        if ([string]::IsNullOrWhiteSpace($RunAsPassword)) {
            throw "ServicePassword is required when ServiceAccount is provided."
        }
        $baseCreateOrConfigArgs += @("obj=", $RunAsAccount, "password=", $RunAsPassword)
    }

    if ($null -eq $service) {
        Invoke-Sc -Args (@("create", $Name) + $baseCreateOrConfigArgs)
    }
    else {
        Invoke-Sc -Args (@("config", $Name) + $baseCreateOrConfigArgs)
    }

    Invoke-Sc -Args @("config", $Name, "start=", "delayed-auto")
    Invoke-Sc -Args @("description", $Name, $Description)
    Set-ServiceRecovery -Name $Name
    Set-ServiceEnvironment -Name $Name -Values $EnvironmentVariables

    if ($StartAfterInstall) {
        try {
            $state = (Get-Service -Name $Name).Status
            if ($state -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
                Restart-Service -Name $Name -Force
            }
            else {
                Start-Service -Name $Name
            }
        }
        catch {
            Write-Warning "Service '$Name' could not be started automatically. Install/update still completed. Error: $($_.Exception.Message)"
            try {
                $scm = Get-WinEvent -LogName System -MaxEvents 100 |
                    Where-Object {
                        $_.ProviderName -eq "Service Control Manager" -and
                        $_.Message -like "*$Name*"
                    } |
                    Select-Object -First 1
                if ($null -ne $scm) {
                    Write-Warning "Latest Service Control Manager event for '$Name' (Id=$($scm.Id)): $($scm.Message)"
                }
            }
            catch {
                # best effort diagnostics
            }
        }
    }
}

try {
    Ensure-ElevatedOrRelaunch

    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    }
    if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
        $PublishRoot = Join-Path $RepoRoot "deploy"
    }

    if (-not $PSCmdlet.ShouldProcess("$ApiServiceName, $WorkerServiceName", "Install/update Windows services")) {
        Write-OpResult -Success $true -Step "Service install/update canceled" -Details "No local service changes were made." -NextStep "Rerun and confirm when ready."
        Pause-IfRequested -NoPause:$NoPause
        exit 0
    }
    Assert-Admin

    if ($PublishFirst) {
        & (Join-Path $PSScriptRoot "publish-release.ps1") -RepoRoot $RepoRoot -OutputRoot $PublishRoot -NoPause
        if ($LASTEXITCODE -ne 0) {
            throw "Publish step failed."
        }
    }

    $apiDir = Join-Path $PublishRoot "api"
    $workerDir = Join-Path $PublishRoot "worker"
    $apiExe = Join-Path $apiDir "SwedesClanTracker.Api.exe"
    $workerExe = Join-Path $workerDir "SwedesClanTracker.Worker.exe"

    if (-not (Test-Path $apiExe)) {
        throw "API publish output missing: $apiExe. Run publish-release.ps1 first or pass -PublishFirst."
    }
    if (-not (Test-Path $workerExe)) {
        throw "Worker publish output missing: $workerExe. Run publish-release.ps1 first or pass -PublishFirst."
    }

    $hasServiceCredential = $ServiceCredential -and $ServiceCredential -ne [System.Management.Automation.PSCredential]::Empty
    $hasServiceAccount = -not [string]::IsNullOrWhiteSpace($ServiceAccount)
    $hasServicePassword = -not [string]::IsNullOrWhiteSpace($ServicePassword)

    if ($UseLocalSystem -and ($hasServiceCredential -or $hasServiceAccount -or $hasServicePassword)) {
        throw "-UseLocalSystem cannot be combined with -ServiceCredential, -ServiceAccount, or -ServicePassword."
    }
    if (-not $UseLocalSystem -and -not $hasServiceCredential -and -not $hasServiceAccount) {
        throw "When -UseLocalSystem is disabled, provide either -ServiceCredential or -ServiceAccount."
    }

    $resolvedServiceAccount = $null
    $resolvedServicePassword = $null
    if (-not $UseLocalSystem -and $hasServiceCredential) {
        $resolvedServiceAccount = $ServiceCredential.UserName
        $resolvedServicePassword = Convert-SecureStringToPlainText -SecureString $ServiceCredential.Password
    }
    elseif (-not $UseLocalSystem -and $hasServiceAccount) {
        if ([string]::IsNullOrWhiteSpace($ServicePassword)) {
            $prompt = Get-Credential -UserName $ServiceAccount -Message "Enter Windows account password for service logon (PIN does not work for services)."
            if ($null -eq $prompt) {
                throw "Service credential prompt was cancelled."
            }
            $resolvedServiceAccount = $prompt.UserName
            $resolvedServicePassword = Convert-SecureStringToPlainText -SecureString $prompt.Password
        }
        else {
            $resolvedServiceAccount = $ServiceAccount
            $resolvedServicePassword = $ServicePassword
        }
    }

    $apiEnv = @(
        "ASPNETCORE_ENVIRONMENT=Production",
        "ASPNETCORE_URLS=http://127.0.0.1:5166"
    )
    $workerEnv = @(
        "DOTNET_ENVIRONMENT=Production"
    )

    if ($ConnectionString) {
        $apiEnv += "ConnectionStrings__DefaultConnection=$ConnectionString"
        $workerEnv += "ConnectionStrings__DefaultConnection=$ConnectionString"
    }
    if ($TempleApiKey) {
        $apiEnv += "TempleOsrs__ApiKey=$TempleApiKey"
        $workerEnv += "TempleOsrs__ApiKey=$TempleApiKey"
    }
    if ($WiseOldManVerificationCode) {
        $apiEnv += "WiseOldMan__VerificationCode=$WiseOldManVerificationCode"
        $workerEnv += "WiseOldMan__VerificationCode=$WiseOldManVerificationCode"
    }
    if ($DiscordBotToken) {
        $workerEnv += "DiscordBot__Token=$DiscordBotToken"
    }
    if ($DiscordAdminRoleId) {
        $workerEnv += "DiscordBot__AdminRoleId=$DiscordAdminRoleId"
    }
    if ($AuthUsername) {
        $apiEnv += "Auth__Username=$AuthUsername"
    }
    if ($AuthPassword) {
        $apiEnv += "Auth__Password=$AuthPassword"
    }

    Install-Or-UpdateService `
        -Name $ApiServiceName `
        -DisplayName "Swedes Clan Tracker API" `
        -Description "Swedes Clan Tracker ASP.NET Core API service." `
        -ExecutablePath $apiExe `
        -EnvironmentVariables $apiEnv `
        -UseLocalSystem:$UseLocalSystem `
        -RunAsAccount $resolvedServiceAccount `
        -RunAsPassword $resolvedServicePassword

    Install-Or-UpdateService `
        -Name $WorkerServiceName `
        -DisplayName "Swedes Clan Tracker Worker" `
        -Description "Swedes Clan Tracker background sync and Discord bot service." `
        -ExecutablePath $workerExe `
        -EnvironmentVariables $workerEnv `
        -UseLocalSystem:$UseLocalSystem `
        -RunAsAccount $resolvedServiceAccount `
        -RunAsPassword $resolvedServicePassword

    Write-Host ""
    Write-Host "Services installed/updated:"
    Write-Host "  $ApiServiceName"
    Write-Host "  $WorkerServiceName"
    Write-Host ""
    Write-Host "Use scripts/windows/check-services.ps1 to verify status."
    Write-OpResult -Success $true -Step "Windows service install/update completed" -Details "ApiService=$ApiServiceName, WorkerService=$WorkerServiceName" -NextStep "Run check-services.ps1 to verify state and API probe."
    Pause-IfRequested -NoPause:$NoPause
}
catch {
    Write-OpResult -Success $false -Step "Windows service install/update failed" -Details $_.Exception.Message -NextStep "Fix the reported issue, then rerun install-services.ps1."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
