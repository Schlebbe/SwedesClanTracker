# Brassican rank-policy transition

**Completed on 2026-11-01.** This is the historical execution and recovery
runbook, not an outstanding cutover checklist. Tracker verification passed,
services resumed, and the user closed Discord follow-up after accepting two
account-match exceptions as alts or incorrect accounts. Private completion
evidence, manifests, journals, and reports remain outside Git. Normal future
promotions follow the [permanent policy](../../../docs/RANK_POLICY.md).

This is the approved runbook for the one-time transition from the retired
`Recruit`/`Officer`/`Astral` policy to the canonical Brassican policy. It is an
operator-led cutover. The normal worker sync must not be used as a bulk
migration mechanism. In the final cutover plan, explicitly qualifying
promotions may be marked `APPROVED_FOR_CUTOVER` so the database, the
manually-confirmed WOM observation, and the manual in-game list converge on
one final rank. The game roster is authoritative; the user updates the game
and the configured/external WOM sync reflects that result. The transition tool
only checks the frozen WOM expectation before writing tracker data. Discord
role IDs and frozen role-name renames remain part of the cutover, but Discord
member-role changes are a separate manual follow-up by default. That label
means a reviewed planned commit in the frozen manifest; it is not a live
approval or a service deployment.

## Canonical policy

Ranks are ordered from lowest to highest as follows:

`Brassican`, `Commander`, `Lieutenant`, `Captain`, `General`, `Colonel`,
`Brigadier`, `Admiral`, `Marshal`, `Beast`.

Brassican is the assignable, ordinary fallback rank. A player gets the highest
rank for which at least one requirement is met; each row below uses inclusive
OR matching.

| Rank | Eligibility |
| --- | --- |
| Brassican | Default when no higher requirement matches |
| Commander | Total level >= 2200 |
| Lieutenant | EHB >= 750 OR EHP >= 1000 |
| Captain | EHB >= 1000 OR EHP >= 1500 OR pets >= 10 OR collections >= 800 |
| General | EHB >= 1250 OR EHP >= 1750 OR pets >= 15 OR collections >= 900 |
| Colonel | EHB >= 1500 OR EHP >= 2000 OR pets >= 25 OR collections >= 1050 |
| Brigadier | EHB >= 2000 OR EHP >= 2500 OR pets >= 35 OR collections >= 1200 |
| Admiral | EHB >= 3000 OR EHP >= 4000 OR pets >= 45 OR collections >= 1350 |
| Marshal | EHB >= 4000 OR EHP >= 5000 OR pets >= 55 OR collections >= 1450 |
| Beast | EHB >= 5000 OR EHP >= 7000 OR pets >= 65 OR collections >= 1550 |

Eligibility is not the same as current standing. A fresh snapshot may produce
a higher eligible rank, but the player's current rank is not silently lowered.
The final cutover plan uses the explicit `--approve-qualifying-promotions`
option to mark safe, qualifying upgrades for this one-time cutover. The
manifest records that planned decision and the report calls it
`APPROVED_FOR_CUTOVER`; no live approval is performed by the report or tool.
The library option remains opt-in by default, and future rolling promotions
continue through the normal approval flow.

There is no earned demotion in this transition. The only intentional baseline
collapse is the retired database `CurrentRank` values `Recruit` and `Officer`
to `Brassican`. The old `Astral` value maps to `General`, and the old
`General` value maps to `Colonel`; those are frozen policy mappings, not
deductions from player progress. External/special WOM roles, including legacy
WOM `Recruit`, stay protected and are never treated as ordinary Brassican.

## Frozen mappings

The transition manifest stores `legacy-rank-transition-v1`. Apply, retry,
verify, and rollback must use the manifest's source values and operation IDs.
Do not infer a source rank from whatever text happens to be present after a
partial rename.

| System | Original value | Frozen target | Notes |
| --- | --- | --- | --- |
| Database `CurrentRank` | Recruit | Brassican | Retired ordinary baseline |
| Database `CurrentRank` | Officer | Brassican | Retired ordinary baseline |
| Database `CurrentRank` | Astral | General | Standing-rank identity mapping |
| Database `CurrentRank` | General | Colonel | Standing-rank identity mapping |
| Normal WOM role | Officer | Brassican | User/external WOM sync must show the final role |
| Normal WOM role | Astral | General | User/external WOM sync must show the final role |
| Normal WOM role | General | Colonel | User/external WOM sync must show the final role |
| Discord role name | Officer | Brassican | Rename the existing role ID |
| Discord role name | Astral | General | Rename the existing role ID |
| Discord role name | General | Colonel | Rename the existing role ID |
| Database `EligibleRank` | Latest snapshot via new evaluator | Evaluator result | Recompute from current stats; never legacy-remap the new result |

`EligibleRank` is not a second `CurrentRank` mapping. With a usable latest
snapshot, `RankEvaluator` returns a canonical rank directly; the transition
must preserve that exact result and must not run it through the legacy map.
If there is no usable snapshot, hold the row for review rather than guessing a
new eligibility value.

There is deliberately no normal WOM or Discord mapping for `Recruit`. Legacy
WOM `Recruit` is a protected external/special role, not an ordinary Brassican
role to rename. It must remain untouched.

The mapping is independent per system. A manually confirmed WOM role update, a
database value normalization, a Discord role rename, and a worker
configuration-key update are separate operations with separate verification.
The transition tool does not write WOM roles in standard mode. Never use a
current WOM or Discord label to reinterpret an old operation: an old `General`
role is the role that becomes `Colonel`; an old `Astral` role is the role that
becomes `General`, even after one of those names already exists elsewhere.

## Audit expectations and holds

The fresh normal-WOM audit used for this policy recorded 114 ordinary WOM
Officers: 77 map to Brassican and 37 map to Commander. The 26 low-level
database rows still labelled legacy `Recruit` are tracked as transition data;
they are not evidence that Brassican is a special WOM role. A prior audit had
91 database-only Officer rows. Keep these counts as audit context, not as
synthetic production metrics.

There are 35 open ignored rank-mismatch cases. Preserve each ignore and its
history. Do not auto-resolve, overwrite, or silently convert them; include
them in the review queue after the transition. Also hold for review when any
of the following is true:

- the WOM observation is not fresh, unavailable, rate-limited, or identity is
  ambiguous; or it does not match the frozen manual final-role expectation;
- the account is WOM-only, missing from the database, stale according to
  `LastSynced`/source health, or has an open rename/merge identity review;
- the WOM role is special/unknown (including legacy `Recruit`), or an ignored
  mismatch is open. A snapshot timestamp alone is not a stale signal when the
  stats themselves have not changed;
- a Discord role target collides with a different role ID. Discord member
  identity gaps are recorded in the full-roster manual review report and do
  not block the approved tracker/database finalization after the read-only WOM
  check; they must be resolved before any later manual member-role change;
- a snapshot/rank value is missing or changed after the manifest was created.

The manifest is the reviewed source of truth. A blocking warning or any
before-value drift stops apply; it is not permission to guess.

Ignored mismatches preserve their ignore status and recorded difference, but
they do not block an unambiguous equivalent legacy database/Discord mapping.
Holds prevent a forced database alignment or a promotion upgrade; they do not
prevent independent tier-equivalent renames captured by the manifest. The
manual WOM value must still be read-only verified before the tracker database
is written. For example, a player may have database source
`CurrentRank=Astral` and WOM source role `General`: the manifest may map the
database standing to `General` and expect the manually updated WOM role
`Colonel` while keeping the ignored mismatch open for review. The two changes
must never be inferred from post-rename text.

## Member cutover report

Every reviewed plan must include human-readable member reports beside the
frozen manifest, for example
`~/.rank-transition/brassican-transition.members.csv` and
`~/.rank-transition/brassican-transition.members.md`. They are generated from
the manifest only; they do not query the database, WOM, or Discord and they do
not approve or mutate anything. CSV keeps machine-readable empty fields and
every stored player/WOM-only row, including `WomRoleAfter`,
`InGameCutoverTarget`, `ApprovedPromotionTarget`, `ApprovalState`, and
`DiscordReviewReason`. Markdown is the compact operator-facing checklist:
its first section has one clear `Final rank` column, current tracker/WOM role,
included promotion, and `✗` for absent optional values; its later sections
summarize holds/inactive rows, a compact legacy tier-rename hold table, and
separate Discord review. That legacy table shows frozen WOM
`before → expected-after` and tracker `before → after` evidence only; it never
authorizes guessing the actual game role. Discord identity concerns never hide
an approved in-game target.

To regenerate these frozen-manifest member sidecars after a report-only fix,
use the read-only command below. It reloads and verifies the supplied manifest,
writes only the `.members.csv`/`.members.md` sidecars, and performs no
database, WOM, Discord, or manifest mutation; do not re-plan or change the
manifest hash:

```powershell
& .\tools\migrations\2026-11-01-brassican\invoke-pi-rank-transition.ps1 -Command member-report `
  -ManifestFileName brassican-transition.json -NoPause
```

Approved plans also include a full Discord-member review report beside those
files, for example
`~/.rank-transition/brassican-transition.discord-members.csv` and
`~/.rank-transition/brassican-transition.discord-members.md`. It lists every
observed guild account with its current managed rank roles, suggested target
role(s), identity status, and any unmatched tracker players. An unresolved
identity is a manual-review row, not permission to guess and not a cutover
blocker. The standard approved plan performs no `DiscordMemberRankRole`
mutation; this report is the handoff for the post-cutover manual review.

The `TrackerRankAfter`/`Final rank` value is the actual final rank
planned by the frozen manifest, not merely the legacy baseline mapping. For
example, approved `Arvld` is `Officer -> Commander` directly: the report must
not show a Brassican target or a separate pending Commander upgrade. The
`FrozenSourceRole` column still shows whether a source came from
`WOM:General`, `WOM:Astral`, or another frozen source. This provenance remains
important because database `Astral -> General` and WOM `General -> Colonel`
are independent mappings.

Use the `Action` column as follows:

- `CUTOVER_REVIEW` lists active players who need a manual in-game roster
  check. For an approved promotion, `ApprovedPromotionTarget` and
  `InGameCutoverTarget` are the same final rank, and `ApprovalState` is
  `APPROVED_FOR_CUTOVER`. Verify the actual in-game identity before changing
  it; this is a planned cutover target, not a live approval.
- `PENDING_APPROVAL` remains for library/default plans and future rolling
  promotions. In the standard final cutover plan, qualifying upgrades use
  `APPROVED_FOR_CUTOVER` instead, so the 37 Commander candidates are not left
  in a normal approval queue for this cutover.
- `NO_CHANGE` lists an active player whose final tracker/WOM standing already
  matches. It has no `InGameCutoverTarget`; an unchanged rank never receives a
  manual game instruction.
- `HOLD` covers protected/special/missing WOM roles, ignored mismatches,
  identity conflicts, stale or unavailable observations, and WOM-only rows.
  A hold is not a blanket instruction to skip every game conversion: some
  held rows still have independent, unambiguous WOM expected targets and
  Discord legacy rename operations. Ordinary legacy-equivalent standing rows
  whose mapped original database rank equals the frozen WOM target remain
  actionable even when eligibility is below that grandfathered standing; they
  are tier renames, not promotions or demotions. Individually compare each
  such row's frozen game `RoleBefore`/`RoleAfter` with the actual game role.
  Keep ignored, special, identity, and stale holds held; an unresolved
  required legacy game conversion blocks the cutover rather than leaving an
  old game `General` meaning beside a manually confirmed WOM `Colonel`.
- `INACTIVE_NO_CHANGE` keeps inactive stored players in the full list while
  explicitly requesting no in-game change.

The report is a manual cutover list, not a live approval screen. The initial
audit context remains 77 Brassican/37 Commander WOM Officers. In the standard
approved cutover mode, those qualifying Commander upgrades are represented as
one final rank and are not separate pending candidates; after cutover, normal
future promotions return to the existing approval queue. The 37 approved rows
each represent one final in-game rank change; the frozen WOM target is a
manual precondition that must be freshly observed before tracker finalization,
not a WOM write performed by the tool. No Discord member-role mutation is
implied by that approval.

## Operator sequence

Use the repository Pi helpers and the transition executable. On the Pi, use
`tools/migrations/2026-11-01-brassican/invoke-pi-rank-transition.ps1` to run the already-published
tool. It reads the private worker environment without shell-sourcing or
printing credentials. It loads non-secret production Worker defaults
(including the WOM group) from
`/opt/swedesclantracker/worker/appsettings.json` before applying worker.env
overrides, keeps manifests under the SSH user's `~/.rank-transition`, and
never deploys or controls a service. Do not use ad hoc SSH, direct
environment-file edits, or a second Discord client.

1. Verify the working tree and operator inputs. Confirm the selected Pi host,
   database, WOM group, Discord guild, and profile; never place credentials in
   a manifest or commit them. Stage the already-published transition package
   at `/opt/swedesclantracker/tools/rank-transition` before the preparatory
   plan, using the separate manual repository copy procedure if needed. This
   is tool staging only; the wrapper never publishes, deploys, or restarts
   services.
2. Prepare the initial read-only plan and member reports before downtime.
   Review every row, warning, pending promotion, hold, identity concern,
   manual in-game target, and full Discord-member review row with the operators
   who will perform the cutover. This list is preparatory only; it must be
   regenerated after the services are stopped and the fresh backup is
   complete. The transition package must already be available for this first
   read-only plan:

   ```powershell
   & .\scripts\windows\pi\test-pi-ssh.ps1 -NoPause
   & .\scripts\windows\pi\check-pi-db-readonly.ps1 -NoPause
   & .\tools\migrations\2026-11-01-brassican\invoke-pi-rank-transition.ps1 -Command plan -NoPause
   ```

3. Pause the API and worker with the repository helpers and verify both are
   stopped. The transition tool never stops services itself:

   ```powershell
   & .\scripts\windows\pi\control-pi-api.ps1 -Action stop -NoPause
   & .\scripts\windows\pi\control-pi-worker.ps1 -Action stop -NoPause
   ```

4. With both services stopped, take a fresh, restorable PostgreSQL backup
   using the repository's Pi backup procedure. This keeps the backup and
   transition observations on a stable application state. Verify that the new
   dump can be found and is restorable before continuing. Record the backup
   path/checksum in the operator log; do not put secrets in the transition
   manifest. On the Pi, the repository procedure is
   `sudo scripts/linux/run-pi-backup.sh`; inspect the resulting custom-format
   dump with `pg_restore --list <dump-path>` before treating the backup gate as
   verified. Reach the Pi through the repository helper workflow rather than
   an ad hoc SSH command.
5. Revalidate the freeze after the backup by generating the final read-only
   manifest and member report while both services remain stopped. Review this
   final report again; do not apply the preparatory manifest if any player,
   source value, role ID, membership observation, or warning changed. The Pi
   wrapper's default manifest name is
   `~/.rank-transition/brassican-transition.json`:

   ```powershell
   & .\tools\migrations\2026-11-01-brassican\invoke-pi-rank-transition.ps1 -Command plan -NoPause
   ```

   `plan` is read-only with respect to application state. The wrapper's
   standard plan forwards `--approve-qualifying-promotions` and enables the
   manual Discord-member review and manual-WOM reconciliation defaults, so
   qualifying upgrades become one final cutover rank while the full
   guild-member inventory is captured for follow-up. Use
   `-NoApproveQualifyingPromotions` only for an intentionally
   old-mode or diagnostic plan. Standard plans use manual WOM updates: the
   user updates the authoritative in-game roster and lets the configured or
   external WOM sync publish the corresponding role. The tool checks that
   fresh WOM observations match the frozen final expectation before any
   tracker database write and performs no WOM write. Any automated Discord
   member-role mutation requires the explicit opt-in
   `-AutomaticDiscordMembers` and is not part of the standard approved
   workflow. The `-NoDiscord` and `-NoWom` switches forward the executable's
   diagnostic options; a final apply must have fresh observations for every
   system it is changing.

   Once this final manifest is reviewed, it is frozen before any manual
   in-game or WOM rank edits begin. Resume this original manifest and its
   journal for apply/retry; do not re-plan from changed labels. If a before
   value drifts after manual work starts, preserve the original artifacts and
   stop for review/reconciliation rather than guessing from the new text (for
   example, treating a newly observed WOM `General` as the old `General` that
   was frozen to become `Colonel`).

   The final cutover manifest must be schema 2 and must be regenerated from a
   fresh, complete plan before any manual edits. A schema-1 manifest cannot be
   applied by the approved-mode tool. After manual work starts, do not re-plan;
   keep the frozen manifest, report, and journal together.

 6. Reconcile the manual game state before apply, using `CUTOVER_REVIEW` rows
    and individually reviewed held rows with the frozen tracker target. The
    in-game clan-rank roster and identity are authoritative; the frozen
    DB/WOM values are only the reviewed proposal, and this tool has no direct
    game observation source. Check the actual game state for every
    `InGameCutoverTarget`, including approved direct targets such as
    `Arvld -> Commander`, then record any discrepancy before apply. The user
    updates the game and the configured/external WOM sync reflects that result.
    Before apply, the tool performs a fresh read-only WOM check and requires
    each non-held frozen expected final role to match; it does not write WOM.
    Check every held row's actual `RoleBefore`/`RoleAfter`; keep ignored
    mismatches ignored and do not use a hold to force alignment. Record
    exceptions. Do not lower a protected/hold row or change inactive rows.
    Discord member-role rows are reviewed after cutover from the full member
    report; they are not a prerequisite for the approved game/database
    finalization after the read-only WOM check. If a required game conversion
    cannot be verified, do not provide the `GameReconciled` attestation and do
    not continue to apply.
7. Install the prepared, reviewed API/Worker build/artifacts while both
   services remain stopped. Publishing and copying are a separate manual
   repository procedure; this transition wrapper does not deploy anything and
   does not restart a service. For a paused backend-only installation, the
   deployment helper supports the explicit stopped-service gate:

   ```powershell
   & .\scripts\windows\pi\deploy-pi-stack.ps1 -NoRestart -BackendOnly -NoPause
   ```

   `-NoRestart` verifies that API and Worker are inactive before and after
   installation, skips automatic health verification, and never starts or
   reloads a service. `-BackendOnly` leaves the frontend build, upload, and
   frontend permission changes untouched. Omit these switches for the normal
   deployment workflow. The transition package was staged before the initial
   plan in step 1; do not postpone that prerequisite until this step.
8. Remap worker configuration keys independently. Keep role IDs stable:
   `Brassican` receives the old `Officer` role ID, `General` receives the old
   `Astral` role ID, and `Colonel` receives the old `General` role ID. Delete
   the `Astral` key. The tracked templates are
   `deploy/env/worker.env.example` and
   `deploy/env/discord-profiles.example.json`. Apply the profile with the
   explicit no-restart gate while the worker remains paused:

   ```powershell
   & .\scripts\windows\pi\discord\set-pi-discord-profile.ps1 -ProfileName real -NoRestart -NoPause
   ```

   If the reviewed plan already observes those configured role IDs with the
   target names `Brassican`, `General`, and `Colonel`, treat the corresponding
   rename as an idempotent no-op. Keep the existing IDs, permissions,
   hierarchy, and role imagery unchanged; do not create a new role or infer a
   replacement from current text. Keep all Discord memberships unchanged
   during the standard cutover; member-role changes are handled manually from
   the post-cutover full-roster report. The configuration-key remap still
   needs individual review and the explicit no-restart gate.
 9. Apply only with explicit reviewed-by, maintenance-window, verified-backup,
    and manual-game/config attestations. `GameReconciled` records the
    operator's confirmation of the authoritative in-game roster; it does not
    claim that the tool observed the game. Standard apply first performs the
    frozen read-only WOM expectation check described in step 6; a mismatch
    stops before any tracker database transaction. The applier then uses
    separate, auditable phases: one database transaction applies the frozen
    baseline and eligibility changes and supersedes the exact old pending
    candidates; the frozen Discord role-ID renames are performed and verified
    separately; then a final database transaction commits each approved final
    promotion target and its owned `APPROVED` history. Standard mode performs
    no WOM write, emits no Discord member-role operation, performs no member
    reassignment, does not perform a live approval, and does not create a
    second pending upgrade:

   ```powershell
   & .\tools\migrations\2026-11-01-brassican\invoke-pi-rank-transition.ps1 -Command apply `
     -ReviewedBy "operator-name" -MaintenanceConfirmed `
     -BackupConfirmed -GameReconciled -NoPause
   ```
10. After apply, verify that Discord preserved the existing role IDs:
   permissions and hierarchy/position must be unchanged, and no role may be
   created or deleted. Confirm that all memberships are unchanged by the
   standard cutover. Role renames are auditable cutover operations; member-role
   changes are a separate post-cutover manual review, not a live approval step.
 11. Verify the database, fresh WOM observations, Discord role IDs/membership,
     full Discord-member review report, worker configuration, and preserved 35
     ignored mismatches. Use the repository health/status helpers while
    services remain paused. Keep the Discord role name exactly `Colonel`; role
    imagery/iconography is a separate manual follow-up and is not changed by
    this transition. Brassican's future cabbage icon is likewise deferred; do
    not add or generate an asset here.
12. Run the final manifest verification while the services remain stopped:

   ```powershell
   & .\tools\migrations\2026-11-01-brassican\invoke-pi-rank-transition.ps1 -Command verify -NoPause
   ```

13. Only after verification, restart the API and worker with the helpers and
    run the full Pi stack verification. When changing a Discord profile during
    the cutover, keep using `-NoRestart` so the worker remains paused until the
    verification gate is complete:

    ```powershell
    & .\scripts\windows\pi\control-pi-api.ps1 -Action start -NoPause
    & .\scripts\windows\pi\control-pi-worker.ps1 -Action start -NoPause
    & .\scripts\windows\pi\verify-pi-stack.ps1 -NoPause
    ```

    The default remains a restart for ordinary profile switches. `-NoRestart`
    is an explicit cutover gate, not a replacement for final service
    verification.

## Discord member follow-up after cutover

After the database, the read-only WOM expectation check, role-ID rename, and
manual in-game gates are verified, regenerate the read-only full-roster
Discord report from the frozen manifest and a fresh guild observation:

```powershell
& .\tools\migrations\2026-11-01-brassican\invoke-pi-rank-transition.ps1 -Command discord-report `
  -ManifestFileName brassican-transition.json -NoPause
```

This command does not re-plan, edit the manifest, or mutate Discord. It keeps
the frozen final tracker/WOM targets and refreshes the current guild-account
columns for review. Review the resulting full-roster Discord report
(`brassican-transition.discord-members.csv` and `.md`) as a separate manual
job. It contains every observed guild account, current managed rank roles,
suggested target role(s), resolved/unresolved tracker identity, and unmatched
tracker rows. Resolve identity questions against the actual account before
changing any member roles; never infer a Discord account from a display-name
collision or a missing match.

The one-time approved cutover already gives each qualifying member one final
in-game rank change; the frozen WOM expectation is manually established and
read-only verified before the corresponding tracker database finalization. It
does not silently imply a Discord member-role change. The current audit is
expected to decompose the 37 qualifying trackers into 33 matched accounts
(27 already at the final Commander role and six needing a managed-rank
update) plus four identity-review rows. Those figures are only the approved-
qualifier subset, not a full-guild total; use the fresh `discord-report`
output for the actual all-account counts. Any automated member-role mutation
path is opt-in and must be separately reviewed; the standard wrapper workflow
leaves memberships unchanged.

## Rolling promotions after cutover

After the one-time cutover, future earned promotions continue through the
normal rolling workflow: refresh stats, review the generated candidate, obtain
the existing approval, update the authoritative in-game roster and let the
configured/external WOM sync reflect it, then make any approved Discord change
with the usual identity and role checks. The transition tool does not write
WOM in standard mode or apply an approved overlay automatically. The 37
Commander candidates are included in the one-time cutover only when the final
plan explicitly uses `--approve-qualifying-promotions`; otherwise they remain
`PENDING` under the library's compatibility/default mode. A joint maintenance
window after the cutover still uses the normal approval flow and must not reuse
a stale schema-1 report.

## Recovery and rollback

Rollback is manifest/journal-driven and must stop on drift. It is not a blind
reverse lookup from current text. If a step partially succeeds, preserve the
journal, stop the services, and identify the exact failed operation. Recovery
must cover the tracker, WOM observation, and Discord state domains together.

In standard manual-WOM mode, rollback changes tracker state and owned Discord
role names only; WOM reconciliation remains a separate manual task.

- restore the verified database backup or apply the journaled database
  compensation;
- manually reconcile the WOM role values using the frozen source/final values
  and identity checks; standard rollback only reads/verifies WOM and does not
  write it;
- restore Discord role names/configuration keys by the same role IDs while
  preserving permissions, hierarchy, and all memberships. Any later manual
  member-role changes are outside this frozen cutover manifest and must be
  reconciled separately.

After compensation, rerun read-only verification for all three domains before
restarting services. If identity, role ID, membership, snapshot, or source
value drift is detected, hold for manual review rather than retrying with
current labels. Keep the manifest, journal, backup evidence, audit output,
and operator decisions together for the final review.

To invoke the tool's compensating rollback, use the same manifest and explicit
attestations:

```powershell
& .\tools\migrations\2026-11-01-brassican\invoke-pi-rank-transition.ps1 -Command rollback `
  -ReviewedBy "operator-name" -MaintenanceConfirmed `
  -BackupConfirmed -GameReconciled -NoPause
& .\tools\migrations\2026-11-01-brassican\invoke-pi-rank-transition.ps1 -Command verify -NoPause
```
