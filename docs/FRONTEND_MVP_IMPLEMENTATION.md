# FRONTEND_MVP_IMPLEMENTATION.md — Codex Plan

Generated: `2026-06-09T11:53:47+00:00`

## Mission

Implement the OSRS Clan Hub frontend MVP while preserving current behavior.

Hard constraint:

> Do not change Core, Worker, database models, migrations, or sync logic.

API changes are optional and should be avoided in the first pass. If needed later, prefer additive read-only DTO projection endpoints.

## Before coding

Codex must read:

- `AGENTS.md`
- `/docs/PRODUCT.md`
- `/docs/DESIGN.md`
- `/docs/swedes_clan_tracker_missing_features.md`
- `/docs/swedes_clan_tracker_scope_review.md`
- the reference images folder

## Recommended file structure

Adjust paths if the project already has better conventions.

```text
swedesclantracker-frontend/src/
  components/
    shell/
      AppShell.jsx
      SidebarNav.jsx
      TopStatusBar.jsx
    osrs/
      BeveledButton.jsx
      DataTable.jsx
      EmptyFeatureState.jsx
      SectionHeader.jsx
      StatCard.jsx
      StatusPill.jsx
      StonePanel.jsx
      UnavailableMetric.jsx
  data/
    viewModels/
      dashboardViewModel.js
      rosterViewModel.js
      reviewQueueViewModel.js
      playerProfileViewModel.js
      activityLogViewModel.js
  styles/
    osrs-theme.css
```

## View-model rule

Pages should call API functions as they do today, then map raw DTOs into UI-friendly shapes.

Example:

```js
const dashboard = mapHomeToDashboardViewModel(home, status);
```

Do not let visual components depend on raw backend DTOs.

## Feature availability rule

Represent unsupported future features explicitly.

Example:

```js
export const featureAvailability = {
  weeklyXp: {
    available: false,
    reason: "Requires TotalXp history from Core/Worker"
  },
  bossKc: {
    available: false,
    reason: "Requires boss KC snapshots"
  },
  skillBreakdown: {
    available: false,
    reason: "Requires per-skill snapshots"
  }
};
```

## Task sequence

### Task 1 — Design system foundation

Implement:
- `osrs-theme.css`
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

Acceptance criteria:
- current pages still render;
- no backend changes;
- no fake production data;
- layout visually moves toward reference images.

### Task 2 — Dashboard surface

Refactor dashboard to use new components.

Use existing data only.

Render:
- tracked/member counts if available;
- pending admin work;
- stale/missing counts if available;
- tracker health/status;
- recent clan activity/log items.

For unsupported mockup metrics:
- hide;
- or show unavailable state.

Do not fake:
- weekly XP;
- boss KC;
- collection-log sync percentage;
- split logs;
- competitions.

### Task 3 — Members roster

Refactor Members page into OSRS roster layout.

Use existing roster API data.

Columns for MVP:
- RSN
- Clan Rank
- Total
- Last Sync
- Status
- Flags
- Actions

Only render these if real data exists:
- Combat
- Build
- Last Gain
- Sync Status Reason

### Task 4 — Review queues

Refactor admin queue into card-based review queues.

Use existing admin queue and review data.

Tabs:
- Possible RSN Changes
- Missing Members
- Rank Reviews

Advanced fields like confidence percent, total XP delta, and boss KC match must be optional and hidden when unavailable.

### Task 5 — Player profile

Refactor profile into OSRS profile shell.

Use existing player profile API data.

Render:
- username;
- rank/status;
- last seen/synced;
- current snapshot values;
- open cases;
- recent events.

Render future sections as unavailable or hidden:
- total XP;
- combat level;
- skill breakdown;
- boss KC;
- recent drops;
- rank history;
- admin notes.

### Task 6 — Activity log polish

Refactor clan log/activity log into a compact OSRS table.

Use existing lifecycle/clan-log data.

Map event tone safely:
- success;
- warning;
- danger;
- info;
- neutral.

Unknown event types must not crash rendering.

## Testing checklist

- App builds.
- Existing API calls still work.
- Login/logout behavior still works.
- Empty API responses render gracefully.
- Missing optional fields do not crash pages.
- Buttons without endpoints are hidden or disabled.
- No fake stats appear in production UI.
- No Core/Worker files changed.
- No migrations added.
- No external design dependencies added without approval.

## Definition of done

The frontend-only MVP is done when:

- the app visually matches the OSRS Clan Hub direction;
- existing surfaces remain functional;
- unsupported metrics are hidden or clearly unavailable;
- all code is organized into reusable components;
- future backend fields can be added through view-model mappers without rewriting page layouts.
