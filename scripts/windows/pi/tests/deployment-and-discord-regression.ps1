[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:Passed = 0
$script:Failed = 0
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($Actual -is [array] -or $Expected -is [array]) {
        $actualText = (@($Actual) -join "|")
        $expectedText = (@($Expected) -join "|")
        if ($actualText -cne $expectedText) {
            throw "$Message (actual '$actualText', expected '$expectedText')"
        }
        return
    }
    if ($Actual -cne $Expected) {
        throw "$Message (actual '$Actual', expected '$Expected')"
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $text = if ($Value -is [array]) { $Value -join "`n" } else { [string]$Value }
    if ($text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        throw "$Message (missing '$Needle')"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $text = if ($Value -is [array]) { $Value -join "`n" } else { [string]$Value }
    if ($text.IndexOf($Needle, [StringComparison]::Ordinal) -ge 0) {
        throw "$Message (unexpected '$Needle')"
    }
}

function Write-TestCall {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][hashtable]$Data
    )

    $record = [ordered]@{ Kind = $Kind; Data = $Data }
    $line = $record | ConvertTo-Json -Compress -Depth 10
    Add-Content -LiteralPath $env:SCT_TEST_LOG -Value $line -Encoding utf8
}

function New-StubPiCommon {
    return @'
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-TestCall {
    param([string]$Kind, [hashtable]$Data)
    $record = [ordered]@{ Kind = $Kind; Data = $Data }
    ($record | ConvertTo-Json -Compress -Depth 10) | Add-Content -LiteralPath $env:SCT_TEST_LOG -Encoding utf8
}

function Resolve-PiHost { param([string]$HostOrIp) return $HostOrIp }
function Resolve-PiUser { param([string]$User) return $User }
function Resolve-PathWithPrompt { param([string]$PathValue, [string]$PromptLabel) return $PathValue }
function Pause-IfRequested { param([switch]$NoPause) }
function Write-OpResult {
    param([bool]$Success, [string]$Step, [string]$Details = "", [string]$NextStep = "")
    Write-TestCall -Kind "result" -Data @{ Success = $Success; Step = $Step; Details = $Details; NextStep = $NextStep }
}

function Invoke-Ssh {
    param(
        [string]$HostOrIp,
        [string]$User,
        [string]$KeyPath,
        [string]$KnownHostsPath,
        [string]$RemoteCommand
    )

    Write-TestCall -Kind "ssh" -Data @{ HostOrIp = $HostOrIp; User = $User; RemoteCommand = $RemoteCommand }
    if ($env:SCT_FAIL_STOPPED -eq "1" -and $RemoteCommand.Contains("base64 -d")) {
        return [PSCustomObject]@{ ExitCode = 1; Output = @("mocked inactive-state guard failure") }
    }
    if ($RemoteCommand -match "cat /etc/swedesclantracker/worker\.env") {
        return [PSCustomObject]@{ ExitCode = 0; Output = @($env:SCT_EXISTING_ENV -split "`n") }
    }
    if ($env:SCT_FAIL_STOP -eq "1" -and $RemoteCommand.Contains("systemctl stop")) {
        return [PSCustomObject]@{ ExitCode = 1; Output = @("mocked service-stop failure") }
    }
    if ($env:SCT_FAIL_SSH -eq "1") {
        return [PSCustomObject]@{ ExitCode = 1; Output = @("mocked SSH failure") }
    }
    if ($env:SCT_FAIL_DEPLOY -eq "1" -and $RemoteCommand.Contains("systemctl start")) {
        return [PSCustomObject]@{ ExitCode = 1; Output = @("mocked deployment failure") }
    }
    if ($RemoteCommand -eq "echo ok") {
        return [PSCustomObject]@{ ExitCode = 0; Output = @("ok") }
    }
    if ($RemoteCommand.Contains("systemctl is-active")) {
        return [PSCustomObject]@{ ExitCode = 0; Output = @("active", "active", "active") }
    }
    if ($RemoteCommand.Contains("systemctl is-enabled")) {
        return [PSCustomObject]@{ ExitCode = 0; Output = @("enabled", "enabled", "enabled") }
    }
    if ($RemoteCommand.Contains("curl -s")) {
        return [PSCustomObject]@{ ExitCode = 0; Output = @("401") }
    }
    return [PSCustomObject]@{ ExitCode = 0; Output = @() }
}

function Invoke-ScpUpload {
    param(
        [string]$LocalPath,
        [string]$RemotePath,
        [string]$HostOrIp,
        [string]$User,
        [string]$KeyPath,
        [string]$KnownHostsPath,
        [switch]$Recurse
    )

    $contentBase64 = ""
    if (Test-Path -LiteralPath $LocalPath -PathType Leaf) {
        $contentBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($LocalPath))
    }
    Write-TestCall -Kind "scp" -Data @{ LocalPath = $LocalPath; RemotePath = $RemotePath; Recurse = $Recurse.IsPresent; ContentBase64 = $contentBase64 }
    if ($env:SCT_FAIL_SCP -eq "1") {
        return [PSCustomObject]@{ ExitCode = 1; Output = @("mocked SCP failure") }
    }
    return [PSCustomObject]@{ ExitCode = 0; Output = @() }
}
'@
}

function New-StubConfigScript {
    return @'
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$HostOrIp,
    [string]$User,
    [string]$KeyPath,
    [string]$KnownHostsPath,
    [string]$DiscordToken,
    [string]$DiscordAdminRoleId,
    [string]$DiscordGuildId,
    [string]$DiscordChannelId,
    [string]$DiscordPetHiscoresChannelId,
    [string[]]$DiscordRankRoleIds = @(),
    [switch]$ClearDiscordRankRoleIds,
    [switch]$NoPause,
    [switch]$NoRestart
)
$record = [ordered]@{
    Kind = "config-stub"
    Data = [ordered]@{
        HostOrIp = $HostOrIp
        User = $User
        DiscordToken = $DiscordToken
        RankRoleIds = @($DiscordRankRoleIds)
        ClearDiscordRankRoleIds = $ClearDiscordRankRoleIds.IsPresent
        NoPause = $NoPause.IsPresent
        NoRestart = $NoRestart.IsPresent
    }
}
($record | ConvertTo-Json -Compress -Depth 10) | Add-Content -LiteralPath $env:SCT_TEST_LOG -Encoding utf8
if ($env:SCT_STUB_CONFIG_EXIT -eq "1") { exit 1 }
exit 0
'@
}

function New-StubVerifyScript {
    return @'
[CmdletBinding()]
param([string]$HostOrIp, [string]$User, [string]$KeyPath, [string]$KnownHostsPath, [switch]$NoPause)
$record = [ordered]@{ Kind = "verify-stub"; Data = @{ HostOrIp = $HostOrIp; User = $User; NoPause = $NoPause.IsPresent } }
($record | ConvertTo-Json -Compress -Depth 10) | Add-Content -LiteralPath $env:SCT_TEST_LOG -Encoding utf8
exit 0
'@
}

function New-StubExternalCommand {
    param([Parameter(Mandatory = $true)][string]$Name)
    return @"
@echo off
>>"%SCT_EXTERNAL_LOG%" echo EXTERNAL^|$Name^|%*
exit /b 0
"@
}

function New-TestFixture {
    param([switch]$ConfigStub)

    $root = Join-Path ([IO.Path]::GetTempPath()) ("sct-pi-script-tests-" + [Guid]::NewGuid().ToString("N"))
    $pi = Join-Path $root "scripts\windows\pi"
    $discord = Join-Path $pi "discord"
    $tests = Join-Path $pi "tests"
    $deployEnv = Join-Path $root "deploy\env"
    $deployOutput = Join-Path $root "deploy\pi"
    $bin = Join-Path $root "mock-bin"
    $log = Join-Path $root "calls.jsonl"
    $externalLog = Join-Path $root "external.log"
    $key = Join-Path $root "mock-key"
    $knownHosts = Join-Path $root "mock-known-hosts"
    New-Item -ItemType Directory -Force -Path $discord, $tests, $deployEnv, $deployOutput, $bin | Out-Null
    Write-Utf8NoBom -Path $key -Content "mock key"
    Write-Utf8NoBom -Path $knownHosts -Content "mock known host"
    Write-Utf8NoBom -Path $log -Content ""
    Write-Utf8NoBom -Path $externalLog -Content ""

    Copy-Item -LiteralPath (Join-Path $script:RepoRoot "scripts\windows\pi\deploy-pi-stack.ps1") -Destination (Join-Path $pi "deploy-pi-stack.ps1")
    Copy-Item -LiteralPath (Join-Path $script:RepoRoot "scripts\windows\pi\discord\set-pi-discord-config.ps1") -Destination (Join-Path $discord "set-pi-discord-config.ps1")
    Copy-Item -LiteralPath (Join-Path $script:RepoRoot "scripts\windows\pi\discord\set-pi-discord-profile.ps1") -Destination (Join-Path $discord "set-pi-discord-profile.ps1")
    Copy-Item -LiteralPath (Join-Path $script:RepoRoot "scripts\windows\pi\discord\switch-pi-discord-real.ps1") -Destination (Join-Path $discord "switch-pi-discord-real.ps1")
    Copy-Item -LiteralPath (Join-Path $script:RepoRoot "scripts\windows\pi\discord\switch-pi-discord-temporary.ps1") -Destination (Join-Path $discord "switch-pi-discord-temporary.ps1")
    Write-Utf8NoBom -Path (Join-Path $pi "pi-common.ps1") -Content (New-StubPiCommon)
    Write-Utf8NoBom -Path (Join-Path $pi "verify-pi-stack.ps1") -Content (New-StubVerifyScript)
    if ($ConfigStub) {
        Write-Utf8NoBom -Path (Join-Path $discord "set-pi-discord-config.ps1") -Content (New-StubConfigScript)
    }

    Write-Utf8NoBom -Path (Join-Path $bin "dotnet.cmd") -Content (New-StubExternalCommand -Name "dotnet")
    Write-Utf8NoBom -Path (Join-Path $bin "npm.cmd") -Content (New-StubExternalCommand -Name "npm")
    Write-Utf8NoBom -Path (Join-Path $bin "ssh.cmd") -Content "@echo raw ssh was unexpectedly invoked 1>&2`r`n@exit /b 97`r`n"
    Write-Utf8NoBom -Path (Join-Path $bin "scp.cmd") -Content "@echo raw scp was unexpectedly invoked 1>&2`r`n@exit /b 97`r`n"
    New-Item -ItemType Directory -Force -Path (Join-Path $root "SwedesClanTracker.Api"), (Join-Path $root "SwedesClanTracker.Worker"), (Join-Path $root "swedesclantracker-frontend") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $deployOutput "api"), (Join-Path $deployOutput "worker"), (Join-Path $deployOutput "frontend"), (Join-Path $root "swedesclantracker-frontend\dist") | Out-Null
    Write-Utf8NoBom -Path (Join-Path $root "SwedesClanTracker.Api\SwedesClanTracker.Api.csproj") -Content "mock"
    Write-Utf8NoBom -Path (Join-Path $root "SwedesClanTracker.Worker\SwedesClanTracker.Worker.csproj") -Content "mock"
    Write-Utf8NoBom -Path (Join-Path $root "swedesclantracker-frontend\package-lock.json") -Content "{}"
    Write-Utf8NoBom -Path (Join-Path $root "swedesclantracker-frontend\dist\index.html") -Content "mock frontend"

    $existingEnv = @(
        "# preserved comment",
        "Other__Value=preserved",
        "Clan__Name=Ångström",
        "DiscordBot__Enabled=true",
        "DiscordBot__Token=old-token",
        "DiscordBot__AdminRoleId=111",
        "DiscordBot__GuildId=222",
        "DiscordBot__ChannelId=333",
        "DiscordBot__PetHiscoresChannelId=444",
        "DiscordBot__RankRoleIds__Officer=901"
    ) -join "`n"

    $profiles = @{
        real = @{
            AdminRoleId = "111"
            GuildId = "222"
            ChannelId = "333"
            PetHiscoresChannelId = "444"
            RankRoleIds = @{ Brassican = "101"; Commander = "102" }
        }
        temporary = @{
            AdminRoleId = "211"
            GuildId = "222"
            ChannelId = "233"
            PetHiscoresChannelId = "244"
            RankRoleIds = @{}
        }
    }
    Write-Utf8NoBom -Path (Join-Path $deployEnv "discord-profiles.json") -Content ($profiles | ConvertTo-Json -Depth 10)

    return [PSCustomObject]@{
        Root = $root
        Pi = $pi
        Discord = $discord
        DeployOutput = $deployOutput
        Bin = $bin
        Log = $log
        ExternalLog = $externalLog
        Key = $key
        KnownHosts = $knownHosts
        ExistingEnv = $existingEnv
    }
}

function Remove-TestFixture {
    param([Parameter(Mandatory = $true)]$Fixture)
    $resolvedRoot = [IO.Path]::GetFullPath($Fixture.Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $parent = [IO.Path]::GetDirectoryName($resolvedRoot)
    $leaf = [IO.Path]::GetFileName($resolvedRoot)
    if (-not [string]::Equals($parent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $leaf -notmatch '^sct-pi-script-tests-[0-9a-f]{32}$') {
        throw "Refusing to remove an unvalidated test fixture path: $resolvedRoot"
    }
    if (Test-Path -LiteralPath $resolvedRoot) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force -ErrorAction Stop
    }
}

function Invoke-IsolatedScript {
    param(
        [Parameter(Mandatory = $true)]$Fixture,
        [Parameter(Mandatory = $true)][string]$RelativeScript,
        [string[]]$Arguments = @(),
        [hashtable]$Environment = @{}
    )

    Set-Content -LiteralPath $Fixture.Log -Value "" -Encoding utf8
    Set-Content -LiteralPath $Fixture.ExternalLog -Value "" -Encoding utf8
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = (Get-Command pwsh).Source
    $psi.ArgumentList.Add("-NoProfile")
    $psi.ArgumentList.Add("-NonInteractive")
    $psi.ArgumentList.Add("-File")
    $psi.ArgumentList.Add((Join-Path $Fixture.Root $RelativeScript))
    foreach ($argument in @($Arguments)) {
        $psi.ArgumentList.Add([string]$argument)
    }
    $psi.WorkingDirectory = $Fixture.Root
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Environment["SCT_TEST_LOG"] = $Fixture.Log
    $psi.Environment["SCT_EXTERNAL_LOG"] = $Fixture.ExternalLog
    $psi.Environment["SCT_EXISTING_ENV"] = $Fixture.ExistingEnv
    $psi.Environment["SCT_FAIL_SSH"] = ""
    $psi.Environment["SCT_FAIL_SCP"] = ""
    $psi.Environment["SCT_FAIL_DEPLOY"] = ""
    $psi.Environment["SCT_FAIL_STOPPED"] = ""
    $psi.Environment["SCT_FAIL_STOP"] = ""
    $psi.Environment["SCT_STUB_CONFIG_EXIT"] = ""
    $psi.Environment["PI_HOST_OR_IP"] = "mock-pi"
    $psi.Environment["PI_USER"] = "mock-user"
    $psi.Environment["DISCORD_BOT_TOKEN"] = "mock-token"
    $psi.Environment["PATH"] = $Fixture.Bin + [IO.Path]::PathSeparator + $env:PATH
    foreach ($entry in $Environment.GetEnumerator()) {
        $psi.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [PSCustomObject]@{ ExitCode = $process.ExitCode; StdOut = $stdout; StdErr = $stderr }
}

function Read-TestCalls {
    param([Parameter(Mandatory = $true)]$Fixture)
    if (-not (Test-Path -LiteralPath $Fixture.Log)) { return @() }
    return @(Get-Content -LiteralPath $Fixture.Log | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Read-ExternalLog {
    param([Parameter(Mandatory = $true)]$Fixture)
    if (-not (Test-Path -LiteralPath $Fixture.ExternalLog)) { return @() }
    return @(Get-Content -LiteralPath $Fixture.ExternalLog | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Decode-RemoteScript {
    param([Parameter(Mandatory = $true)][string]$Command)
    $match = [regex]::Match($Command, "printf '%s' '([^']+)' \| base64 -d \| bash")
    if (-not $match.Success) { throw "Expected a base64 Bash remote command, got: $Command" }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($match.Groups[1].Value))
}

function Invoke-Case {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Body
    )

    try {
        & $Body
        $script:Passed++
        Write-Host "PASS $Name"
    }
    catch {
        $script:Failed++
        Write-Host "FAIL $Name :: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Invoke-Case "all scripts parse" {
    $paths = @(
        "scripts/windows/pi/deploy-pi-stack.ps1",
        "scripts/windows/pi/discord/set-pi-discord-config.ps1",
        "scripts/windows/pi/discord/set-pi-discord-profile.ps1",
        "scripts/windows/pi/discord/switch-pi-discord-real.ps1",
        "scripts/windows/pi/discord/switch-pi-discord-temporary.ps1"
    )
    foreach ($relative in $paths) {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $script:RepoRoot $relative), [ref]$tokens, [ref]$errors) | Out-Null
        Assert-Equal -Actual $errors.Count -Expected 0 -Message "Parser errors in $relative"
    }
}

Invoke-Case "default deployment publishes all and verifies health" {
    $fixture = New-TestFixture
    try {
        $result = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\deploy-pi-stack.ps1" -Arguments @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-RepoRoot", $fixture.Root, "-NoPause", "-Confirm:$false"
        )
        Assert-Equal -Actual $result.ExitCode -Expected 0 -Message "Default deployment should succeed"
        $calls = Read-TestCalls $fixture
        $sshCommands = @($calls | Where-Object Kind -eq "ssh" | ForEach-Object { $_.Data.RemoteCommand })
        $deployCommand = $sshCommands | Where-Object { $_ -match "systemctl start swedesclantracker-api" } | Select-Object -First 1
        Assert-True -Condition ($null -ne $deployCommand) -Message "Default deployment must issue restart command"
        Assert-Contains -Value $deployCommand -Needle "set -e" -Message "Default deployment must fail fast"
        Assert-Contains -Value $deployCommand -Needle "/frontend/" -Message "Default deployment must install frontend"
        Assert-Contains -Value ($calls.Kind) -Needle "verify-stub" -Message "Default deployment must run health verification"
        $external = Read-ExternalLog $fixture
        Assert-True -Condition (@($external | Where-Object { $_ -match "EXTERNAL\|dotnet\|publish" }).Count -eq 2) -Message "Default deployment must publish API and Worker"
        Assert-True -Condition (@($external | Where-Object { $_ -match "EXTERNAL\|npm\|ci" }).Count -eq 1) -Message "Default deployment must install frontend dependencies"
        Assert-True -Condition (@($external | Where-Object { $_ -match "EXTERNAL\|npm\|run build" }).Count -eq 1) -Message "Default deployment must build frontend"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "BackendOnly skips frontend while retaining restart" {
    $fixture = New-TestFixture
    try {
        $result = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\deploy-pi-stack.ps1" -Arguments @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-RepoRoot", $fixture.Root, "-SkipPublish", "-BackendOnly", "-SkipVerify", "-NoPause", "-Confirm:$false"
        )
        Assert-Equal -Actual $result.ExitCode -Expected 0 -Message "BackendOnly deployment should succeed"
        $calls = Read-TestCalls $fixture
        $sshCommands = @($calls | Where-Object Kind -eq "ssh" | ForEach-Object { $_.Data.RemoteCommand })
        $deployCommand = $sshCommands | Where-Object { $_ -match "systemctl start swedesclantracker-api" } | Select-Object -First 1
        Assert-NotContains -Value $deployCommand -Needle "/frontend/" -Message "BackendOnly deployment must omit frontend"
        Assert-Contains -Value $deployCommand -Needle "systemctl start swedesclantracker-api" -Message "BackendOnly deployment must restart services"
        Assert-True -Condition (@($calls | Where-Object { $_.Kind -eq "scp" -and $_.Data.LocalPath -match "frontend" }).Count -eq 0) -Message "BackendOnly must not upload frontend"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "Combined NoRestart and BackendOnly skips npm and preserves frontend" {
    $fixture = New-TestFixture
    try {
        $sentinel = Join-Path $fixture.DeployOutput "frontend\sentinel.txt"
        Write-Utf8NoBom -Path $sentinel -Content "keep frontend"
        $result = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\deploy-pi-stack.ps1" -Arguments @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-RepoRoot", $fixture.Root, "-BackendOnly", "-NoRestart", "-NoPause", "-Confirm:$false"
        )
        Assert-Equal -Actual $result.ExitCode -Expected 0 -Message "Combined NoRestart/BackendOnly deployment should succeed"
        Assert-Equal -Actual (Get-Content -LiteralPath $sentinel -Raw) -Expected "keep frontend" -Message "BackendOnly publish must preserve frontend output"
        $external = Read-ExternalLog $fixture
        Assert-True -Condition (@($external | Where-Object { $_ -match "EXTERNAL\|dotnet\|publish" }).Count -eq 2) -Message "BackendOnly publish must still prepare API and Worker"
        Assert-True -Condition (@($external | Where-Object { $_ -match "EXTERNAL\|npm\|" }).Count -eq 0) -Message "BackendOnly publish must skip npm"
        $sshCommands = @(Read-TestCalls $fixture | Where-Object Kind -eq "ssh" | ForEach-Object { $_.Data.RemoteCommand })
        $install = Decode-RemoteScript $sshCommands[2]
        Assert-NotContains -Value $install -Needle "/frontend/" -Message "Combined NoRestart/BackendOnly install must omit frontend"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "NoRestart uses guarded base64 payloads and never starts services" {
    $fixture = New-TestFixture
    try {
        $result = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\deploy-pi-stack.ps1" -Arguments @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-RepoRoot", $fixture.Root, "-SkipPublish", "-SkipVerify", "-NoRestart", "-NoPause", "-Confirm:$false"
        )
        Assert-Equal -Actual $result.ExitCode -Expected 0 -Message "NoRestart deployment should succeed"
        $sshCommands = @(Read-TestCalls $fixture | Where-Object Kind -eq "ssh" | ForEach-Object { $_.Data.RemoteCommand })
        $preflight = Decode-RemoteScript $sshCommands[0]
        $install = Decode-RemoteScript $sshCommands[2]
        Assert-Contains -Value $preflight -Needle "}`ncheck_services_stopped" -Message "NoRestart preflight must terminate the function before invocation"
        Assert-NotContains -Value $preflight -Needle "}check_services_stopped" -Message "NoRestart preflight must not concatenate shell tokens"
        Assert-NotContains -Value $preflight -Needle "`r" -Message "NoRestart preflight payload must be LF-normalized"
        Assert-Contains -Value $install -Needle "set -eu" -Message "NoRestart install must fail fast"
        Assert-Equal -Actual ([regex]::Matches($install, "(?m)^check_services_stopped$").Count) -Expected 2 -Message "NoRestart install must check before and after installation"
        Assert-NotContains -Value $install -Needle "systemctl start" -Message "NoRestart must not start services"
        Assert-NotContains -Value $install -Needle "systemctl reload" -Message "NoRestart must not reload services"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "NoRestart guard failure stops before upload" {
    $fixture = New-TestFixture
    try {
        $result = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\deploy-pi-stack.ps1" -Arguments @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-RepoRoot", $fixture.Root, "-SkipPublish", "-NoRestart", "-NoPause", "-Confirm:$false"
        ) -Environment @{ SCT_FAIL_STOPPED = "1" }
        Assert-Equal -Actual $result.ExitCode -Expected 1 -Message "NoRestart guard failure must fail the script"
        $calls = Read-TestCalls $fixture
        Assert-Equal -Actual (@($calls | Where-Object Kind -eq "ssh").Count) -Expected 1 -Message "Guard failure must stop after preflight"
        Assert-Equal -Actual (@($calls | Where-Object Kind -eq "scp").Count) -Expected 0 -Message "Guard failure must not upload artifacts"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "Deployment WhatIf performs no remote or local deployment work" {
    $fixture = New-TestFixture
    try {
        $result = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\deploy-pi-stack.ps1" -Arguments @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-RepoRoot", $fixture.Root, "-SkipPublish", "-NoPause", "-WhatIf"
        )
        Assert-Equal -Actual $result.ExitCode -Expected 0 -Message "Deployment WhatIf should exit successfully"
        Assert-Equal -Actual @((Read-TestCalls $fixture)).Count -Expected 0 -Message "Deployment WhatIf must not call SSH/SCP"
        Assert-Equal -Actual @((Read-ExternalLog $fixture)).Count -Expected 0 -Message "Deployment WhatIf must not publish"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "Default deployment failure propagates instead of reporting success" {
    $fixture = New-TestFixture
    try {
        $result = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\deploy-pi-stack.ps1" -Arguments @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-RepoRoot", $fixture.Root, "-SkipPublish", "-SkipVerify", "-NoPause", "-Confirm:$false"
        ) -Environment @{ SCT_FAIL_DEPLOY = "1" }
        Assert-Equal -Actual $result.ExitCode -Expected 1 -Message "Deployment SSH failure must propagate"
        $failureResult = (Read-TestCalls $fixture | Where-Object { $_.Kind -eq "result" -and -not $_.Data.Success } | Select-Object -Last 1)
        Assert-True -Condition ($null -ne $failureResult) -Message "Deployment failure must be reported"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "Default deployment stop failure is not masked" {
    $fixture = New-TestFixture
    try {
        $result = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\deploy-pi-stack.ps1" -Arguments @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-RepoRoot", $fixture.Root, "-SkipPublish", "-SkipVerify", "-NoPause", "-Confirm:$false"
        ) -Environment @{ SCT_FAIL_STOP = "1" }
        Assert-Equal -Actual $result.ExitCode -Expected 1 -Message "Default deployment stop failure must propagate"
        $sshCommands = @(Read-TestCalls $fixture | Where-Object Kind -eq "ssh" | ForEach-Object { $_.Data.RemoteCommand })
        $deployCommand = $sshCommands | Where-Object { $_ -match "systemctl stop swedesclantracker-api" } | Select-Object -First 1
        Assert-NotContains -Value $deployCommand -Needle "|| true" -Message "Default deployment must not mask stop failure"
        Assert-Equal -Actual (@((Read-TestCalls $fixture) | Where-Object Kind -eq "verify-stub").Count) -Expected 0 -Message "Stop failure must prevent health verification"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "Discord config default and NoRestart branches preserve UTF8 and fail fast" {
    $fixture = New-TestFixture
    try {
        $commonArguments = @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-DiscordToken", "new-token", "-DiscordAdminRoleId", "111", "-DiscordGuildId", "222",
            "-DiscordChannelId", "333", "-DiscordPetHiscoresChannelId", "444", "-NoPause", "-Confirm:$false"
        )
        $default = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\set-pi-discord-config.ps1" -Arguments $commonArguments
        Assert-Equal -Actual $default.ExitCode -Expected 0 -Message "Default Discord config update should succeed: $($default.StdOut) $($default.StdErr)"
        $defaultCalls = Read-TestCalls $fixture
        $defaultApply = @($defaultCalls | Where-Object Kind -eq "ssh" | Select-Object -Last 1).Data.RemoteCommand
        Assert-Contains -Value $defaultApply -Needle "set -e" -Message "Default Discord config update must fail fast"
        Assert-Contains -Value $defaultApply -Needle "systemctl stop swedesclantracker-worker" -Message "Default Discord config must stop Worker"
        Assert-Contains -Value $defaultApply -Needle "systemctl start swedesclantracker-worker" -Message "Default Discord config must restart Worker"
        Assert-Contains -Value $defaultApply -Needle "systemctl is-active swedesclantracker-worker" -Message "Default Discord config must verify Worker"
        $uploaded = @($defaultCalls | Where-Object Kind -eq "scp" | Select-Object -Last 1).Data.ContentBase64
        $uploadedBytes = [Convert]::FromBase64String($uploaded)
        Assert-True -Condition (-not ($uploadedBytes.Length -ge 3 -and $uploadedBytes[0] -eq 0xEF -and $uploadedBytes[1] -eq 0xBB -and $uploadedBytes[2] -eq 0xBF)) -Message "worker.env upload must not contain a UTF8 BOM"
        Assert-Contains -Value ([Text.Encoding]::UTF8.GetString($uploadedBytes)) -Needle "Ångström" -Message "worker.env upload must preserve Swedish UTF8"
        Assert-Contains -Value ([Text.Encoding]::UTF8.GetString($uploadedBytes)) -Needle "DiscordBot__RankRoleIds__Officer=901" -Message "Omitted rank-role input must preserve existing rank keys"

        $noRestart = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\set-pi-discord-config.ps1" -Arguments ($commonArguments + "-NoRestart")
        Assert-Equal -Actual $noRestart.ExitCode -Expected 0 -Message "NoRestart Discord config update should succeed"
        $noRestartApply = @((Read-TestCalls $fixture) | Where-Object Kind -eq "ssh" | Select-Object -Last 1).Data.RemoteCommand
        Assert-Contains -Value $noRestartApply -Needle "set -e" -Message "NoRestart Discord config update must fail fast"
        Assert-NotContains -Value $noRestartApply -Needle "systemctl stop" -Message "NoRestart Discord config must not stop Worker"
        Assert-NotContains -Value $noRestartApply -Needle "systemctl start" -Message "NoRestart Discord config must not start Worker"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "Unchanged Discord config is a no-op" {
    $fixture = New-TestFixture
    try {
        $arguments = @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-DiscordToken", "old-token", "-DiscordAdminRoleId", "111", "-DiscordGuildId", "222",
            "-DiscordChannelId", "333", "-DiscordPetHiscoresChannelId", "444", "-NoPause", "-Confirm:$false"
        )
        $result = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\set-pi-discord-config.ps1" -Arguments $arguments
        Assert-Equal -Actual $result.ExitCode -Expected 0 -Message "Unchanged Discord config should succeed"
        $calls = Read-TestCalls $fixture
        Assert-Equal -Actual (@($calls | Where-Object Kind -eq "ssh").Count) -Expected 1 -Message "Unchanged Discord config should only read existing env"
        Assert-Equal -Actual (@($calls | Where-Object Kind -eq "scp").Count) -Expected 0 -Message "Unchanged Discord config must not upload"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "Discord config WhatIf and apply failure have no false success" {
    $fixture = New-TestFixture
    try {
        $arguments = @(
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-KeyPath", $fixture.Key, "-KnownHostsPath", $fixture.KnownHosts,
            "-DiscordToken", "new-token", "-DiscordAdminRoleId", "111", "-DiscordGuildId", "222",
            "-DiscordChannelId", "333", "-DiscordPetHiscoresChannelId", "444", "-NoPause", "-Confirm:$false"
        )
        $whatIf = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\set-pi-discord-config.ps1" -Arguments ($arguments + "-WhatIf")
        Assert-Equal -Actual $whatIf.ExitCode -Expected 0 -Message "Discord config WhatIf should succeed"
        Assert-Equal -Actual @((Read-TestCalls $fixture)).Count -Expected 0 -Message "Discord config WhatIf must not read or upload config"

        $failed = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\set-pi-discord-config.ps1" -Arguments $arguments -Environment @{ SCT_FAIL_SSH = "1" }
        Assert-Equal -Actual $failed.ExitCode -Expected 1 -Message "Discord config apply failure must propagate"
        $failureResult = (Read-TestCalls $fixture | Where-Object { $_.Kind -eq "result" -and -not $_.Data.Success } | Select-Object -Last 1)
        Assert-True -Condition ($null -ne $failureResult) -Message "Discord config apply failure must be reported"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "Profile and real/temporary switches use named splats and forward flags" {
    $fixture = New-TestFixture -ConfigStub
    try {
        $profile = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\set-pi-discord-profile.ps1" -Arguments @(
            "-ProfileName", "real", "-ProfilesPath", (Join-Path $fixture.Root "deploy\env\discord-profiles.json"),
            "-HostOrIp", "mock-pi", "-User", "mock-user", "-DiscordToken", "profile-token", "-NoPause", "-NoRestart", "-Confirm:$false"
        )
        Assert-Equal -Actual $profile.ExitCode -Expected 0 -Message "Direct profile switch should succeed"
        $profileCall = (Read-TestCalls $fixture | Where-Object Kind -eq "config-stub" | Select-Object -Last 1)
        Assert-Equal -Actual (@($profileCall.Data.RankRoleIds) | Sort-Object) -Expected (@("Brassican=101", "Commander=102") | Sort-Object) -Message "Profile rank roles must arrive as separate string[] values"
        Assert-True -Condition $profileCall.Data.NoRestart -Message "Direct profile must forward NoRestart"
        Assert-True -Condition $profileCall.Data.NoPause -Message "Direct profile must forward NoPause"
        Assert-NotContains -Value $profileCall.Data.RankRoleIds -Needle "-ProfileName" -Message "Profile must not pass named tokens positionally"

        $real = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\switch-pi-discord-real.ps1" -Arguments @("-NoPause", "-NoRestart", "-Confirm:$false")
        Assert-Equal -Actual $real.ExitCode -Expected 0 -Message "Real profile switch should succeed"
        $realCall = (Read-TestCalls $fixture | Where-Object Kind -eq "config-stub" | Select-Object -Last 1)
        Assert-True -Condition $realCall.Data.NoRestart -Message "Real switch must forward NoRestart"

        $temporary = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\switch-pi-discord-temporary.ps1" -Arguments @("-NoPause", "-Confirm:$false")
        Assert-Equal -Actual $temporary.ExitCode -Expected 0 -Message "Temporary profile switch should succeed"
        $temporaryCall = (Read-TestCalls $fixture | Where-Object Kind -eq "config-stub" | Select-Object -Last 1)
        Assert-True -Condition $temporaryCall.Data.ClearDiscordRankRoleIds -Message "Empty temporary rank map must preserve Clear semantics"
        Assert-True -Condition (-not $temporaryCall.Data.NoRestart) -Message "Default temporary switch must not invent NoRestart"
    }
    finally { Remove-TestFixture $fixture }
}

Invoke-Case "Discord wrapper WhatIf and child failure have no side effects" {
    $fixture = New-TestFixture -ConfigStub
    try {
        $whatIf = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\switch-pi-discord-real.ps1" -Arguments @("-NoPause", "-WhatIf")
        Assert-Equal -Actual $whatIf.ExitCode -Expected 0 -Message "Wrapper WhatIf should succeed"
        Assert-Equal -Actual @((Read-TestCalls $fixture)).Count -Expected 0 -Message "Wrapper WhatIf must not invoke nested config script"

        $failed = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\switch-pi-discord-real.ps1" -Arguments @("-NoPause", "-Confirm:$false") -Environment @{ SCT_STUB_CONFIG_EXIT = "1" }
        Assert-Equal -Actual $failed.ExitCode -Expected 1 -Message "Wrapper child failure must propagate"

        $failedTemporary = Invoke-IsolatedScript -Fixture $fixture -RelativeScript "scripts\windows\pi\discord\switch-pi-discord-temporary.ps1" -Arguments @("-NoPause", "-Confirm:$false") -Environment @{ SCT_STUB_CONFIG_EXIT = "1" }
        Assert-Equal -Actual $failedTemporary.ExitCode -Expected 1 -Message "Temporary wrapper child failure must propagate"
    }
    finally { Remove-TestFixture $fixture }
}

Write-Host "`nScript regression summary: $script:Passed passed, $script:Failed failed."
if ($script:Failed -gt 0) { exit 1 }
exit 0
