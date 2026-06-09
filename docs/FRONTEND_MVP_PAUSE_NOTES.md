# Frontend MVP Pause Notes

Generated: `2026-06-09T14:27:51+00:00`

## Current status

The frontend-only OSRS Clan Hub MVP sequence is paused after Prompt 6.

```text
Prompt 1 — OSRS shell/components        Completed
Prompt 2 — Dashboard MVP                Completed
Prompt 3 — Members roster MVP           Completed
Prompt 4 — Review Queues MVP            Completed
Prompt 5 — Player Profile MVP           Completed
Prompt 6 — Activity Log polish          Completed
Prompt 7 — Cleanup/consistency pass     Not started
```

## Completed prompts and review notes

### Prompt 1 — OSRS design-system foundation

Implemented:
- OSRS theme tokens and legacy-compatible styling.
- Shared shell: `AppShell`, `SidebarNav`, `TopStatusBar`.
- Shared OSRS components: `BeveledButton`, `DataTable`, `EmptyFeatureState`, `StatCard`, `StatusPill`, `StonePanel`, `UnavailableMetric`.
- Authenticated app wrapped in the new shell.
- Current loading, auth, navigation, data fetching, and page behavior preserved.
- Settings and Support disabled.
- New Event not added.

Intentional omissions:
- No page redesigns yet.
- No fake XP, combat, boss KC, drops, collection log, confidence scoring, or future metrics.
- No new actions without real endpoint behavior.
- No Core, Worker, API, database, migration, sync, Discord, deployment, secret, or endpoint-contract changes.

Review notes:
- Top bar status may still depend on Dashboard-specific polling; later consider global polling.
- Sidebar brand is acceptable as static copy for now, but can later come from config or `/api/app/home`.
- CSS supports both new and legacy classes during migration; later reduce legacy selectors.
- Some CSS uses newer browser features like `color-mix()` and `text-wrap`; confirm target browser support if needed.

### Prompt 2 — Dashboard MVP

Implemented:
- `mapHomeToDashboardViewModel(home, liveStatus)`.
- Dashboard refactored to shared OSRS components.
- Rendered supported data: tracker/API/worker health, latest sync/event, member counts, pending promotions, open admin cases, roster posture counts, admin work preview, recent meaningful changes.
- Added unavailable states for weekly XP, boss KC, collection-log sync, drops/splits, and competitions.

Intentional omissions:
- No fake XP, KC, drops, competitions, split logs, or collection-log accuracy.
- No other page redesigns.
- No quick-tool actions or new endpoints.
- No backend/Core/Worker changes.

Review notes:
- `StatusBlock` may display tone words like `success`/`warning`; later use display labels like `Healthy`, `Stale`, `Open`, `Pending`.
- Too many unavailable/future cards can visually overpower real data; review after cleanup.
- Worker/live-status parsing exists in multiple places; later extract a shared mapper.

### Prompt 3 — Members roster MVP

Implemented:
- `mapRosterToRosterViewModel(rows)`.
- Members refactored into OSRS roster layout.
- Rendered real roster API fields: RSN/username, clan rank, status, last sync, last seen, flags, Open Profile action.
- Dynamic status filtering from real returned statuses.
- Roster summary counts: total rows, stale sync, review cases, promotions, rank mismatches.
- Dense roster CSS for long usernames, sticky headers, date cells, and responsive behavior.

Intentional omissions:
- No total level column because current roster API does not expose it.
- No combat level, account build, last gain, sync status reason.
- No mock data, fake pagination, row sync/edit actions.
- No other page redesigns.
- No backend/Core/Worker changes.

Review notes:
- `statusTone()` should normalize case defensively with `value.toUpperCase()`.
- Rows with no flags get a `clear`/info-style pill; later decide between `success` or `neutral`.
- `Open Profile` assumes all rows have IDs; likely correct, but a safe guard is low-risk.

### Prompt 4 — Review Queues MVP

Implemented:
- `mapAdminQueueToReviewQueueViewModel`.
- Admin Queue refactored into OSRS-style Review Queues.
- Grouped data into Possible RSN Changes, Missing Members, Rank Reviews, and fallback Other Reviews.
- Rendered real fields: title/type, player, risk, confidence label, age, recommended action, evidence, alternatives, dangerous action notes.
- Detail panel explains direct case actions are unavailable when app queue exposes guidance but not executable action contracts.

Intentional omissions:
- No advanced RSN confidence scoring.
- No fake confidence percentages.
- No fake XP/KC/HiScores/snapshot metrics.
- No Ask Member action.
- No fake approve/reject/link buttons.
- No other page redesigns.
- No backend/Core/Worker changes.

Review notes:
- A case may appear in multiple groups if it matches multiple bucket keywords. Later assign each case to the first matching group only.
- Review card buttons should use `type="button"` where relevant.
- List keys should have safe fallbacks if an ID is ever missing.

### Prompt 5 — Player Profile MVP

Implemented:
- `mapPlayerProfileToViewModel(player)`.
- Player Profile refactored into OSRS-style profile shell.
- Rendered real fields: username, current rank, eligible rank when present, lifecycle/status, last seen, last synced, open cases, recent events.
- Latest snapshot handling only renders real snapshot fields exposed by the payload.

Intentional omissions:
- No fake total XP, combat level, skill data, boss KC, drops, rank history, admin notes.
- No fake buttons/actions such as Update HiScores, Promotion Review, Save Notes, or activity filters.
- No new endpoints.
- No backend/Core/Worker changes.

Review notes:
- `statusTone()` should normalize case defensively.
- The unavailable profile modules panel may be visually heavy; consider collapsing or showing fewer modules.
- `Eligible Rank` as a stat card may feel empty when absent; review visually.

### Prompt 6 — Activity Log polish

Implemented:
- `mapClanLogToActivityLogViewModel`.
- Clan Log refactored into OSRS-style Activity Log table.
- Used existing `/api/app/clan-log` data only.
- Rendered real fields: humanized time from `time`, event title/type from `title`/`group`, details from `detail`, status/tone mapped from real groups.
- Added filters using existing API filter values.
- Routine sync/system entries included only under Sync/System and All.
- Unknown event groups fall back to neutral tone.
- Empty filtered logs render through `DataTable` / `EmptyFeatureState`.

Intentional omissions:
- No fake drops, splits, boss KC, XP gains, competitions, event actions.
- No member column because current clan-log DTO does not expose a separate member/player field.
- No action column because current clan-log DTO does not expose a real action target.
- No other page redesigns.
- No backend/Core/Worker changes.

Review notes:
- Inspect the Prompt 6 commit before Prompt 7.
- Confirm filters are understandable and not hiding important events by default.
- Confirm neutral fallback tone looks acceptable for unknown groups.

## Cross-cutting intentionally left-out features

Unsupported stats/tracking:
- Total XP, weekly XP, XP deltas.
- Combat level, account build, per-skill breakdown.
- Boss KC, raid KC, recent drops, split logs.
- Collection-log item details and sync accuracy.
- Competitions/leagues.
- Last gain and sync status reason unless exposed by API.

Unsupported RSN review metrics:
- Advanced confidence percentages.
- HiScores match percentage.
- Boss KC match percentage.
- Total XP matching.
- Last snapshot matching.
- Automatic link/reject actions unless executable action contracts exist.

Unsupported admin/product workflows:
- Ask Member messaging.
- Update HiScores from profile.
- Promotion Review from profile.
- Save Admin Notes.
- Run Audit.
- Clear Temp Cache.
- Add Member / New Event.
- Export / Sync buttons unless backed by real endpoints.

Backend/domain work deferred:
- Core model changes.
- Worker sync changes.
- Database migrations.
- Stat snapshot expansion.
- Boss KC persistence.
- Skill snapshot persistence.
- Drop/split entities.
- Competition entities.
- Notification center/RBAC.
- New sync/lifecycle behavior.

## Suggested Prompt 7 focus

Prompt 7 should be cleanup and consistency only:
- Inspect Prompt 6 commit first.
- Normalize repeated helpers where low-risk: status tone mapping, date/time formatting, unavailable/future module rendering.
- Check repeated CSS and consolidate where safe.
- Make sure all pages consistently use shared OSRS components.
- Make sure unsupported metrics are hidden or shown through `UnavailableMetric`.
- Make sure no fake production data was introduced.
- Make sure buttons/actions are disabled/hidden unless real behavior exists.
- Make sure `App.jsx` is not becoming a monolith.
- Check responsive behavior and long username/table readability.
- Document future backend/API work instead of implementing it.

Suggested copy/paste prompt:

```text
First inspect the Prompt 6 commit and summarize any issues.

Then proceed with Prompt 7 only: frontend cleanup and consistency pass.

Scope:
- Review App shell, Dashboard, Members, Review Queues, Player Profile, and Activity Log.
- Ensure shared OSRS components are used consistently.
- Reduce duplicated CSS where safe.
- Normalize obvious repeated helpers where low-risk.
- Ensure unsupported future metrics are hidden or rendered through unavailable states.
- Ensure no fake production stats or actions were introduced.
- Keep the pass small and low-risk.

Do not change Core, Worker, API controllers/contracts, database models, migrations, sync logic, Discord behavior, deployment scripts, secrets, or endpoint contracts.

Run:
cd swedesclantracker-frontend
npm run build
npm run lint

Report files changed, fixes made, future work documented, backend safety, and validation results.
```
