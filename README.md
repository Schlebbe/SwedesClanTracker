# SwedesClanTracker

Local full-stack OSRS clan rank tracker:
- `SwedesClanTracker.Api` (ASP.NET Core Web API + cookie auth)
- `SwedesClanTracker.Worker` (continuous roster/stat sync worker + Discord bot)
- `SwedesClanTracker.Core` (EF Core models + rank/lifecycle logic)
- `swedesclantracker-frontend` (React + Vite dashboard)

## Defaults
- Tracker defaults:
  - `Tracker:TempleApiCallsPerMinute = 5`
  - `Tracker:DiscordDeleteDelayMinutes = 5`
  - `Tracker:DiscordDeleteHardCapMinutes = 10`

## Run (Development)
1. Start SQL Server locally.
2. Run API:
   - `dotnet run --project SwedesClanTracker.Api`
3. Run worker:
   - `dotnet run --project SwedesClanTracker.Worker`
4. Run frontend:
   - `cd swedesclantracker-frontend`
   - `npm run dev`

## Run Permanently (Windows Native Services)
Use the provided scripts to run API and Worker as always-on Windows services without Visual Studio debugger.

1. Publish release binaries:
   - `powershell -ExecutionPolicy Bypass -File .\scripts\windows\publish-release.ps1`
2. Install or update services (run elevated PowerShell):
   - `powershell -ExecutionPolicy Bypass -File .\scripts\windows\install-services.ps1 -PublishFirst -UseLocalSystem -ConnectionString "YOUR_CONNECTION_STRING" -TempleApiKey "YOUR_TEMPLE_API_KEY" -WiseOldManVerificationCode "YOUR_WOM_VERIFICATION_CODE" -DiscordBotToken "YOUR_DISCORD_BOT_TOKEN" -DiscordAdminRoleId "YOUR_DISCORD_ADMIN_ROLE_ID" -AuthUsername "YOUR_ADMIN_USER" -AuthPassword "YOUR_ADMIN_PASS"`
3. Check status:
   - `powershell -ExecutionPolicy Bypass -File .\scripts\windows\check-services.ps1`
4. Remove services if needed:
   - `powershell -ExecutionPolicy Bypass -File .\scripts\windows\uninstall-services.ps1`

Service defaults:
- API service name: `SwedesClanTracker-Api`
- Worker service name: `SwedesClanTracker-Worker`
- API bind URL in service mode: `http://127.0.0.1:5166`
- Startup type: `Automatic (Delayed Start)`
- Restart on failure: enabled
- Service manager: built-in Windows Service Control Manager (`sc.exe`)
- Default service account: `LocalSystem` (recommended permanent mode)
- To use custom service credentials instead, pass `-UseLocalSystem:$false` with `-ServiceCredential` or `-ServiceAccount` + `-ServicePassword`

Frontend in permanent setup:
- Keep API + Worker running as services.
- Start frontend only when needed:
  - `cd swedesclantracker-frontend`
  - `npm run dev`

## Notes
- Initial SQL schema script: `SwedesClanTracker.Core/Migrations/001_initial.sql`
- Promotion candidates are created only as `PENDING`; never auto-approved.
- Pet rules implemented:
  - manual override always wins
  - API pet count only increases stored value
  - HTTP 402 from pets endpoint is ignored
- Queue priority order:
  - manual priority (`/update`)
  - `MISSING_PENDING_REVIEW`
  - normal sync queue

## Required Secrets
Set secrets for both projects (`Api` and `Worker`) because both perform Temple/WiseOldMan operations.

API:
- `dotnet user-secrets set "ConnectionStrings:DefaultConnection" "YOUR_CONNECTION_STRING" --project SwedesClanTracker.Api`
- `dotnet user-secrets set "TempleOsrs:ApiKey" "YOUR_KEY" --project SwedesClanTracker.Api`
- `dotnet user-secrets set "WiseOldMan:VerificationCode" "YOUR_WOM_VERIFICATION_CODE" --project SwedesClanTracker.Api`
- `dotnet user-secrets set "Auth:Username" "YOUR_ADMIN_USER" --project SwedesClanTracker.Api`
- `dotnet user-secrets set "Auth:Password" "YOUR_ADMIN_PASS" --project SwedesClanTracker.Api`

Worker:
- `dotnet user-secrets set "ConnectionStrings:DefaultConnection" "YOUR_CONNECTION_STRING" --project SwedesClanTracker.Worker`
- `dotnet user-secrets set "TempleOsrs:ApiKey" "YOUR_KEY" --project SwedesClanTracker.Worker`
- `dotnet user-secrets set "WiseOldMan:VerificationCode" "YOUR_WOM_VERIFICATION_CODE" --project SwedesClanTracker.Worker`
- `dotnet user-secrets set "DiscordBot:Token" "YOUR_DISCORD_BOT_TOKEN" --project SwedesClanTracker.Worker`
- `dotnet user-secrets set "DiscordBot:AdminRoleId" "YOUR_DISCORD_ADMIN_ROLE_ID" --project SwedesClanTracker.Worker`

Service account note:
- In permanent LocalSystem mode, pass secrets as environment variables during install:
  - `TempleOsrs__ApiKey`
  - `WiseOldMan__VerificationCode`
  - `DiscordBot__Token`
  - `DiscordBot__AdminRoleId`
  - `Auth__Username`
  - `Auth__Password`
  - (optional) `ConnectionStrings__DefaultConnection`
- Example install with env-backed secrets:
  - `powershell -ExecutionPolicy Bypass -File .\scripts\windows\install-services.ps1 -PublishFirst -UseLocalSystem -ConnectionString "YOUR_CONNECTION_STRING" -TempleApiKey "YOUR_TEMPLE_API_KEY" -WiseOldManVerificationCode "YOUR_WOM_VERIFICATION_CODE" -DiscordBotToken "YOUR_DISCORD_BOT_TOKEN" -DiscordAdminRoleId "YOUR_DISCORD_ADMIN_ROLE_ID" -AuthUsername "YOUR_ADMIN_USER" -AuthPassword "YOUR_ADMIN_PASS"`
- SQL permission prerequisite for LocalSystem (`Trusted_Connection=True`):
  - Run once in SSMS as SQL admin:
```sql
USE [master];
IF DB_ID(N'Swedes') IS NULL
BEGIN
    CREATE DATABASE [Swedes];
END;

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'NT AUTHORITY\SYSTEM')
BEGIN
    CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
END;

USE [Swedes];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'NT AUTHORITY\SYSTEM')
BEGIN
    CREATE USER [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
END;

ALTER ROLE [db_owner] ADD MEMBER [NT AUTHORITY\SYSTEM];
```
- Troubleshooting:
  - Service Control Manager event `7038` means service logon account/password issue.
  - SQL errors like `CREATE DATABASE permission denied in database 'master'` mean LocalSystem SQL permissions are missing.

## Discord Bot
The worker can post two kinds of actionable embeds:
- Promotion candidates (`Approve`, `Dismiss`, `Mark Rename Suspect`)
- Temple-missing review cards (`Add back to Temple`, `Remove from DB`)

Config (`SwedesClanTracker.Worker/appsettings.json`):
- `DiscordBot:Enabled` (`true/false`)
- `DiscordBot:Token`
- `DiscordBot:AdminRoleId` (set through user secrets or `DiscordBot__AdminRoleId`)
- `DiscordBot:GuildId`
- `DiscordBot:ChannelId`
- `TempleOsrs:GroupId` (default `449`)
- `WiseOldMan:GroupId` (default `7173`)
- `Tracker:DiscordDeleteDelayMinutes` (preferred delete time)
- `Tracker:DiscordDeleteHardCapMinutes` (hard max delete time)

Action behavior:
- `Approve` -> sets candidate `APPROVED`, updates player current rank.
- `Dismiss` -> sets candidate `DISMISSED`.
- `Mark Rename Suspect` -> sets player status `MERGE_SUGGESTED`.
- `Add back to Temple` -> adds to Temple and mirrors to WiseOldMan.
- `Remove from DB` -> removes from WiseOldMan first (or verifies already absent), then removes local player data.

## Frontend Review Actions
Frontend calls backend endpoints only (no direct Temple/WOM calls from browser):
- `POST /api/review/players/{id}/temple-missing/add`
- `POST /api/review/players/{id}/temple-missing/remove-db`

## Discord Message Lifecycle
- Action-required messages stay active until handled.
- Non-active messages are scheduled for deletion:
  - preferred at `DiscordDeleteDelayMinutes` (default 5)
  - hard cap at `DiscordDeleteHardCapMinutes` (default 10)
- Slash command personal responses are also scheduled through DB-backed lifecycle events so cleanup survives restarts.
