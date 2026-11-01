# Rank-policy commit plan

The Brassican transition completed on 2026-11-01. Keep the lasting policy,
reusable operations scripts, and historical migration in three reviewable
commits. The groups below record the commit structure for this cleanup.

## 1. Permanent rank policy and runtime behavior

Suggested title: `Introduce Brassican and the revised rank ladder`

Include:

- Core rank order, inclusive OR evaluator, defaults, and descriptions;
- Worker rank choices, conditions, promotion role handling, and narrow
  compatibility guards for historical Recruit records;
- the related Core, Worker, and API test changes;
- canonical rank keys in the example Worker environment and Discord profiles;
- `docs/RANK_POLICY.md` and the README policy link.

These changes are permanent. API production code, database schema, stat
collection, and sync scheduling are unchanged. Keep legacy compatibility
guards narrow: a retired rank must never become assignable again. The audit
CSV retains its existing `SkippedRecruit` verdict; its explanation identifies
the rank as legacy.

## 2. Reusable Pi deployment and Discord configuration scripts

Suggested title: `Add controlled Pi deployment options and fix profile forwarding`

Include:

- `deploy-pi-stack.ps1` optional `-NoRestart` and `-BackendOnly` switches,
  guarded output cleanup, and failure propagation;
- Discord configuration/profile scripts and real/temporary wrappers, including
  named parameter forwarding, `-NoRestart`, and `-WhatIf` support;
- `scripts/windows/pi/tests/deployment-and-discord-regression.ps1` and README
  usage notes.

Successful default invocations still deploy the full stack, restart services,
and verify health. Discord configuration changes still restart Worker by
default. A failed service stop now aborts deployment rather than being masked.

Deployment `-NoRestart` requires API and Worker to be inactive before and
after installation and skips automatic health checks. Configuration
`-NoRestart` only writes the environment file: it does not stop or start
Worker, and the new settings load at the next deliberate restart.
`-BackendOnly` leaves frontend artifacts and installation untouched.

## 3. Completed migration archive

Suggested title: `Archive the completed Brassican migration and recovery tests`

Include `tools/migrations/2026-11-01-brassican/`, its dedicated solution,
source/tests, Pi wrapper, runbook, README archive link, this commit plan, and
the `.rank-transition/` ignore rule.

The migration is specific to this cutover. Retain it for audit, frozen-manifest
interpretation, and recovery; keep it outside the main solution, normal
deployment, and Worker sync. Future promotions use the permanent policy.
Report commands remain read-only; apply/rollback retain explicit gates and
journal checks. The archive is not a command to rerun the completed cutover.

Keep real manifests, rosters, journals, generated reports, backups, credentials,
and production configuration outside Git. Synthetic test fixtures and example
configuration placeholders are safe to retain.

## Validation before committing

Run from the repository root:

```powershell
dotnet build SwedesClanTracker.slnx
dotnet test SwedesClanTracker.slnx --no-build --no-restore
dotnet build tools/migrations/2026-11-01-brassican/RankTransition.slnx
dotnet test tools/migrations/2026-11-01-brassican/RankTransition.slnx --no-build --no-restore
pwsh -NoProfile -File scripts/windows/pi/tests/deployment-and-discord-regression.ps1
git diff --check
```

The archived PostgreSQL rehearsal requires an isolated local test connection;
see the archive README. Script regressions use temporary fixtures and mocked
SSH, SCP, publish, and verification commands. They do not deploy to the Pi or
restart live services.
