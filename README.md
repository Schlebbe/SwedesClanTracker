# SwedesClanTracker

Local full-stack OSRS clan rank tracker:
- `SwedesClanTracker.Api` (ASP.NET Core Web API + cookie auth)
- `SwedesClanTracker.Worker` (continuous roster/stat sync worker + Discord bot)
- `SwedesClanTracker.Core` (EF Core models + rank/lifecycle logic)
- `swedesclantracker-frontend` (React + Vite dashboard)

Production is Raspberry Pi first: PostgreSQL, `systemd`, and `nginx`. Windows remains supported for local development and optional service hosting, but SQL Server is no longer used.

## Defaults
- Tracker defaults:
  - `Tracker:TempleApiCallsPerMinute = 5`
  - `Tracker:DiscordDeleteDelayMinutes = 5`
  - `Tracker:DiscordDeleteHardCapMinutes = 10`
- Production API bind URL: `http://127.0.0.1:5166`
- Production dashboard: `nginx` on port `80`, intended for LAN-only access.

## Database
The app uses PostgreSQL everywhere.

Connection string format:
```text
Host=localhost;Port=5432;Database=swedesclantracker;Username=swedes;Password=YOUR_PASSWORD
```

For Windows development, use a separate local database such as `swedesclantracker_dev`. The Raspberry Pi production database should live on the Pi and use `localhost` from the Pi's point of view.

## Run Locally On Windows
Prerequisites:
- .NET 10 SDK
- Node.js/npm
- PostgreSQL

Create a local development database using pgAdmin or `psql`. Recommended dev database name:
```text
swedesclantracker_dev
```

Set user secrets for both backend projects.

API:
```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=swedesclantracker_dev;Username=swedes;Password=YOUR_PASSWORD" --project SwedesClanTracker.Api
dotnet user-secrets set "TempleOsrs:ApiKey" "YOUR_KEY" --project SwedesClanTracker.Api
dotnet user-secrets set "WiseOldMan:VerificationCode" "YOUR_WOM_VERIFICATION_CODE" --project SwedesClanTracker.Api
dotnet user-secrets set "Auth:Username" "YOUR_ADMIN_USER" --project SwedesClanTracker.Api
dotnet user-secrets set "Auth:Password" "YOUR_ADMIN_PASS" --project SwedesClanTracker.Api
```

Worker:
```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=swedesclantracker_dev;Username=swedes;Password=YOUR_PASSWORD" --project SwedesClanTracker.Worker
dotnet user-secrets set "TempleOsrs:ApiKey" "YOUR_KEY" --project SwedesClanTracker.Worker
dotnet user-secrets set "WiseOldMan:VerificationCode" "YOUR_WOM_VERIFICATION_CODE" --project SwedesClanTracker.Worker
dotnet user-secrets set "DiscordBot:Token" "YOUR_DISCORD_BOT_TOKEN" --project SwedesClanTracker.Worker
dotnet user-secrets set "DiscordBot:AdminRoleId" "YOUR_DISCORD_ADMIN_ROLE_ID" --project SwedesClanTracker.Worker
```

Run the stack:
```powershell
dotnet run --project SwedesClanTracker.Api
dotnet run --project SwedesClanTracker.Worker
cd swedesclantracker-frontend
npm run dev
```

The API applies EF Core migrations on startup.

## Raspberry Pi Production
The Pi should have:
- PostgreSQL
- nginx
- .NET 10 ASP.NET Core runtime
- rsync and jq
- UFW/fail2ban/unattended-upgrades

The repo includes Pi deployment assets:
- `scripts/linux/publish-release.sh`
- `scripts/linux/create-pi-database.sh`
- `scripts/linux/install-pi-services.sh`
- `scripts/linux/deploy-to-pi.sh`
- `scripts/linux/verify-pi-stack.sh`
- `scripts/linux/run-pi-backup.sh`
- `scripts/linux/install-pi-backup-timer.sh`
- `deploy/systemd/*.service`
- `deploy/nginx/swedesclantracker.conf`
- `deploy/env/*.example`

### Runbook By Intent (Windows-Only, No Linux Commands Needed)
You can run these scripts directly by right-clicking the `.ps1` file and choosing **Run with PowerShell**.
Approve UAC prompts for scripts that stop/start Windows services.

- Deploy Pi stack:
  - `scripts/windows/pi/deploy-pi-stack.ps1`
- Switch Pi worker to temporary Discord:
  - `scripts/windows/pi/discord/set-pi-discord-profile.ps1 -ProfileName temporary`
  - `scripts/windows/pi/discord/switch-pi-discord-temporary.ps1`
- Switch Pi worker to real Discord:
  - `scripts/windows/pi/discord/set-pi-discord-profile.ps1 -ProfileName real`
  - `scripts/windows/pi/discord/switch-pi-discord-real.ps1`
- Show current Pi worker Discord profile:
  - `scripts/windows/pi/discord/get-pi-discord-profile.ps1`
- Verify Pi burn-in health:
  - `scripts/windows/pi/verify-pi-stack.ps1`
- Check redacted Pi runtime env values (safe output):
  - `scripts/windows/pi/get-pi-redacted-env.ps1`
- Manage Pi sudo access bootstrap for Codex/operator automation:
  - `scripts/windows/pi/set-pi-sudo-access.ps1`
- Run promotion posted-event ownership repair (dry-run by default):
  - `scripts/windows/pi/repair-pi-promotion-posted-ownership.ps1`
- Check EF SQL logging verbosity recommendation on Pi:
  - `scripts/windows/pi/check-pi-logging-profile.ps1`
- Control Pi worker quickly:
  - `scripts/windows/pi/control-pi-worker.ps1`
- Control Pi API quickly:
  - `scripts/windows/pi/control-pi-api.ps1`
- Cut over to Pi + real Discord:
  - `scripts/windows/pi/cutover-to-pi-real-discord.ps1`
- Roll back to Windows services:
  - `scripts/windows/pi/rollback-to-windows.ps1`

One-time Pi database setup:
```bash
SWEDES_DB_PASSWORD='use-a-long-random-password' sudo -E scripts/linux/create-pi-database.sh
```

Legacy Windows service maintenance scripts are also click-runnable via **Run with PowerShell**:
- `scripts/windows/check-services.ps1`
- `scripts/windows/publish-release.ps1`
- `scripts/windows/install-services.ps1`
- `scripts/windows/update-services.ps1`
- `scripts/windows/uninstall-services.ps1`

Discord profile switching setup (one-time on your workstation):
```powershell
Copy-Item deploy\env\discord-profiles.example.json deploy\env\discord-profiles.json
```
`deploy/env/discord-profiles.json` is git-ignored so local overrides stay out of source control. The bot token is reused from the Pi's existing `/etc/swedesclantracker/worker.env` unless you explicitly pass `-DiscordToken`.

One-time Pi service/nginx setup, from a clone or copied repo on the Pi:
```bash
sudo scripts/linux/install-pi-services.sh
```

Create production env files from the examples:
```bash
sudo install -m 0600 deploy/env/api.env.example /etc/swedesclantracker/api.env
sudo install -m 0600 deploy/env/worker.env.example /etc/swedesclantracker/worker.env
sudo nano /etc/swedesclantracker/api.env
sudo nano /etc/swedesclantracker/worker.env
```

Build release artifacts from a machine with .NET SDK, Node/npm, bash, and rsync:
```bash
scripts/linux/publish-release.sh
```

Deploy to the Pi:
```bash
scripts/linux/deploy-to-pi.sh sebastian@192.168.10.106 /path/to/ssh/key
```

Check services on the Pi:
```bash
systemctl status swedesclantracker-api swedesclantracker-worker nginx
journalctl -u swedesclantracker-api -f
journalctl -u swedesclantracker-worker -f
scripts/linux/verify-pi-stack.sh sebastian@192.168.10.106 /path/to/ssh/key
```

Allow LAN-only dashboard access if it is not already configured:
```bash
sudo ufw allow from 192.168.10.0/24 to any port 80 proto tcp
```

Do not port-forward the dashboard or API from the router.

## Parallel Migration (Windows Live, Pi Burn-In)
- Keep Windows services + SQL Server + real Swedes Discord live during validation.
- Run Pi against PostgreSQL and temporary Discord while it builds real live state.
- Pi burn-in can keep production Temple/WiseOldMan credentials and group IDs when that is an explicit operator choice.
- Switch Pi Discord values using:
  - `scripts/windows/pi/discord/set-pi-discord-profile.ps1 -ProfileName temporary`
  - `scripts/windows/pi/discord/set-pi-discord-profile.ps1 -ProfileName real`
  - `scripts/windows/pi/discord/get-pi-discord-profile.ps1`
- Cut over or roll back using:
  - `scripts/windows/pi/cutover-to-pi-real-discord.ps1`
  - `scripts/windows/pi/rollback-to-windows.ps1`

## PostgreSQL Backup And Restore
Create a backup on the Pi:
```bash
pg_dump -U swedes -h localhost -Fc swedesclantracker > swedesclantracker.dump
```

Restore into a fresh database:
```bash
createdb -U swedes -h localhost swedesclantracker_restore
pg_restore -U swedes -h localhost -d swedesclantracker_restore swedesclantracker.dump
```

For automated backups, prefer writing dumps to a directory that is copied off the Pi.
To install a daily local backup timer on the Pi:
```bash
sudo scripts/linux/install-pi-backup-timer.sh
```

## Discord Bot
The worker can post actionable embeds for:
- Promotion candidates (`Approve`, `Dismiss`, `Mark Rename Suspect`)
- Temple/WiseOldMan review cards
- Rename/merge review cards
- Pet hiscore updates

Important worker config:
- `DiscordBot:Enabled`
- `DiscordBot:Token`
- `DiscordBot:AdminRoleId`
- `DiscordBot:GuildId`
- `DiscordBot:ChannelId`
- `DiscordBot:PetHiscoresChannelId`
- `TempleOsrs:GroupId`
- `WiseOldMan:GroupId`

Discord only needs outbound internet access from the Pi. No inbound Discord firewall ports are required.

## Notes
- Promotion candidates are created only as `PENDING`; never auto-approved.
- Pet rules implemented:
  - manual override always wins
  - API pet count only increases stored value
  - HTTP 402 from pets endpoint is ignored
- Queue priority order:
  - manual priority (`/update`)
  - `MISSING_PENDING_REVIEW`
  - normal sync queue
- Frontend calls backend endpoints only; no direct Temple/WiseOldMan calls from the browser.
