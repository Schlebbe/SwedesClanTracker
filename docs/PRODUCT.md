# PRODUCT.md — SwedesClanTracker OSRS Clan Hub MVP

Generated: `2026-06-09T11:53:47+00:00`

## Product direction

SwedesClanTracker should evolve from a generic clan tracker console into an **unofficial Old School RuneScape clan admin hub**.

The immediate goal is a **frontend-only MVP**:
- make the app look and navigate like the OSRS Clan Hub reference mockups;
- keep existing behavior working;
- use only data that the current API already provides;
- represent unavailable future data honestly;
- avoid Core, Worker, database, or sync logic changes in this phase.

## Primary users

### Clan owner / deputy owner
Wants a quick view of roster health, pending admin work, stale members, possible name-change cases, promotion candidates, and tracker status.

### Rank reviewer / admin
Wants to review possible RSN changes, missing members, rank candidates, and player state without digging through raw logs.

### Clan event/PvM coordinator
Future user. May eventually care about drops, splits, competitions, boss KC, and activity history. These are not part of the first frontend-only MVP unless real data already exists.

## Product principles

1. **Do not fake tracker data.**
   If the current API does not provide a metric, show an unavailable/future state or omit the metric.

2. **Frontend-first, non-breaking.**
   The first implementation must not modify Core, Worker, database models, migrations, or sync behavior.

3. **Use view models.**
   Pages should map current API DTOs into frontend view models. Do not bind UI components tightly to raw API shapes.

4. **Design for future data.**
   Components may accept optional fields like `combatLevel`, `totalXp`, `bossKc`, or `confidencePercent`, but must render cleanly when those values are `null` or missing.

5. **Keep the app unofficial.**
   Use custom OSRS-inspired styling. Do not require official game art, official logos, or copyrighted UI assets.

## MVP scope

### In scope now

- OSRS-inspired app shell
- Sidebar navigation
- Top sync/status bar
- Dark stone/brass visual theme
- Shared UI components
- Dashboard restyle using existing data
- Members roster restyle using existing roster API data
- Admin queue / review cards using existing admin queue data
- Rank review page or tab using existing promotion endpoints where practical
- Player profile shell using existing player profile data
- Activity log polish using existing lifecycle/clan-log data
- Future/unavailable states for unsupported metrics

### Explicitly out of scope for the frontend-only MVP

- Core model changes
- Worker sync changes
- Database migrations
- New stat collection logic
- Total XP collection
- Combat level collection
- Per-skill snapshots
- Boss KC snapshots
- Drop/split accounting
- Competitions/leagues
- Real collection-log item details
- Advanced RSN confidence scoring
- New messaging integrations for “Ask Member”
- Notification center/RBAC overhaul
- Fake demo data in production paths

## Data honesty rules

Use these labels consistently:

| Situation | UI behavior |
|---|---|
| API provides real value | Render normally |
| API field is missing | Hide the field or show unavailable |
| Feature requires future Core/Worker work | Show `Requires enhanced sync` or `Not tracked yet` |
| Existing data is partial | Render with a tooltip/note explaining the limitation |
| Action endpoint does not exist | Hide or disable the action |
| Mockup shows unsupported metric | Do not hard-code a fake value |

## Feature availability model

The frontend may define a small feature availability registry, for example:

```js
export const featureAvailability = {
  totalXp: {
    available: false,
    reason: "Requires TotalXp in player snapshots"
  },
  combatLevel: {
    available: false,
    reason: "Requires combat level collection"
  },
  bossKc: {
    available: false,
    reason: "Requires boss KC snapshots"
  },
  dropsAndSplits: {
    available: false,
    reason: "Requires a drops/splits source"
  },
  competitions: {
    available: false,
    reason: "Requires competition domain model"
  }
};
```

Components should use this registry or equivalent props to avoid showing fake data.

## Product phases

### Phase 1 — Visual foundation

Deliver shared components and styling:
- `AppShell`
- `SidebarNav`
- `TopStatusBar`
- `StonePanel`
- `StatCard`
- `StatusPill`
- `BeveledButton`
- `DataTable`
- `EmptyFeatureState`
- `UnavailableMetric`

No API/Core/Worker changes.

### Phase 2 — Frontend surfaces

Restyle existing pages:
- Dashboard
- Members
- Admin Queue / Review Queues
- Player Profile
- Clan Log / Activity Log
- Readiness/Status, if still needed

Use view-model mappers.

### Phase 3 — Optional API projection layer

Only if needed, add read-only app-facing DTOs that reshape existing data.
No database, Core, or Worker changes.

### Phase 4 — Future backend/domain milestones

Later, add real support for:
- total XP;
- combat level;
- per-skill snapshots;
- boss KC;
- richer RSN confidence;
- admin notes;
- rank history;
- drops/splits;
- competitions.

## MVP success criteria

- App visually matches the OSRS Clan Hub direction.
- Existing functionality still works.
- No Core/Worker logic changed.
- No fake production data.
- Missing future features are clearly marked unavailable or hidden.
- Codex can implement each task independently from the docs and reference images.
