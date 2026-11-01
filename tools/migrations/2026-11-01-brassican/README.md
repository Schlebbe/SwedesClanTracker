# Brassican rank-transition archive

This dated directory is the completed, one-off rank-transition tool and its
recovery tests from 2026-11-01. It is retained for audit and recovery only;
the main solution intentionally does not build or deploy it as part of normal
tracker operations.

The user closed the remaining Discord follow-up, accepting two account-match
exceptions as alts or incorrect accounts. No actionable transition work
remains. The private completion record retains the supporting observations.

The frozen schema-v2 manifest and journal are the source of truth for any
historical investigation. Keep the original manifest integrity digest intact.
The tool's normal CLI workflow is tracker-only for WOM: in-game ranks are
authoritative, WOM is read as a reconciliation gate, and no WOM update
operation is planned or executed by the standard CLI. Discord member changes
are a manual review roster; role-label observations remain frozen in the
manifest. Do not reinterpret the historical manifest with current live role
names, and do not run `apply` or `rollback` against production without a new,
explicitly reviewed recovery decision and all required gates.

The runtime manifests, journals, backups, and reports remain under the
ignored `.rank-transition/` directory at the repository root (or the private
Pi user's `~/.rank-transition/`). They are not copied into this archive.

## Standalone validation

From the repository root:

```powershell
dotnet build tools/migrations/2026-11-01-brassican/RankTransition.slnx
dotnet test tools/migrations/2026-11-01-brassican/RankTransition.slnx
```

The PostgreSQL rehearsal is opt-in and requires an isolated test connection:

```powershell
# Supply an isolated, local-only connection out of band; do not commit its
# value. Optionally set RANK_TRANSITION_PG_BIN to the local PostgreSQL tools.
$env:RANK_TRANSITION_TEST_CONNECTION = '<isolated-test-connection-string>'
$env:RANK_TRANSITION_PG_BIN = '<local-postgresql-bin-directory>'
dotnet test tools/migrations/2026-11-01-brassican/RankTransition.slnx
```

Never put credentials, private keys, or production connection strings in this
directory. The archived Pi wrapper only invokes an already-published package;
it does not deploy, restart, or start services.
