# DESIGN.md — OSRS Clan Hub Frontend Design Guide

Generated: `2026-06-09T11:53:47+00:00`

## Design target

Create a premium, dark, OSRS-inspired clan admin dashboard.

The UI should feel like:
- medieval clan admin panel;
- dark stone and aged brass;
- compact tracker console;
- Old School RuneScape-inspired, but not an official game screenshot.

The UI should not feel like:
- futuristic SaaS;
- cyberpunk monitoring tool;
- generic Bootstrap admin panel;
- direct copy of official OSRS assets.

## Reference assets

The repo has a reference folder with generated mockups. Codex should inspect those images before implementation.

Use the images as visual direction, not as exact pixel-perfect requirements.

Expected screens from the references:
- Dashboard / Operational Dashboard
- Clan Members roster
- Review Queues / Possible RSN Changes
- Player Profile

## Visual principles

### Palette

Use a dark, warm palette with muted gold/brass accents.

Suggested CSS tokens:

```css
:root {
  --osrs-bg: #0f0d0a;
  --osrs-bg-soft: #17130e;
  --osrs-panel: #1d1710;
  --osrs-panel-2: #241c13;
  --osrs-border: #6f5426;
  --osrs-border-muted: #3c2d18;
  --osrs-gold: #d7b56d;
  --osrs-gold-bright: #f1d486;
  --osrs-text: #f3ead2;
  --osrs-text-muted: #b8a98a;
  --osrs-green: #68c27a;
  --osrs-amber: #d99c3b;
  --osrs-red: #c45f4f;
  --osrs-blue: #7d9fc7;
  --osrs-shadow: rgba(0, 0, 0, 0.45);
}
```

### Typography

Use existing project fonts unless adding fonts is already supported.

Recommended:
- headings: slightly heavier, high-contrast, warm color;
- body: readable sans-serif;
- tables: compact, clear, no decorative text that harms readability.

Do not introduce external font dependencies unless already approved.

### Shape and texture

Use:
- dark panels;
- thin brass borders;
- subtle inset shadows;
- beveled buttons;
- compact cards;
- light parchment/gold highlights;
- minimal glow for active states.

Avoid:
- heavy noise textures that hurt readability;
- neon colors;
- glassmorphism;
- excessive gradients;
- official OSRS interface graphics.

## Shared component guidance

### AppShell

Responsible for:
- page layout;
- sidebar;
- top status bar;
- main content container.

Should not contain page-specific business logic.

### SidebarNav

Labels for MVP:

```text
Dashboard
Clan Members
Player Profiles
Name Changes
Rank Reviews
Activity Log
Settings
Support
```

A `+ New Event` button may be shown only if it has a real action. Otherwise hide or disable it.

### TopStatusBar

Use existing status API where available.

Display:
- HiScores/Tracker sync status
- last snapshot / last sync when available
- search input if currently functional
- current admin label if available

If current user info is not available, use a neutral label or omit.

### StonePanel

Base container component.

Props:
- `title`
- `subtitle`
- `actions`
- `tone`
- `children`

Use for page sections and cards.

### StatCard

Props:
- `label`
- `value`
- `detail`
- `trend`
- `icon`
- `tone`
- `available`
- `unavailableReason`

If `available === false`, render an unavailable state instead of fake values.

### StatusPill

Tones:
- `success`
- `warning`
- `danger`
- `info`
- `neutral`

Use for:
- synced;
- stale;
- pending;
- missing;
- review needed;
- unavailable.

### BeveledButton

Variants:
- `primary`
- `secondary`
- `ghost`
- `danger`
- `disabled`

Buttons must only be enabled when an action exists.

### DataTable

Use for roster and activity log.

Features:
- compact rows;
- sticky-looking header if easy;
- status pills;
- row actions;
- graceful empty state.

## Screen guidance

### Dashboard

Target sections:
- page heading: `Operational Dashboard`
- status/action row
- KPI cards using existing data
- pending admin work
- recent clan activity

Use now:
- tracked members if derivable;
- pending reviews if available;
- stale/missing members if available;
- last sync/status;
- recent lifecycle/clan-log events.

Do not fake:
- weekly XP gained;
- boss KC logged;
- collection-log sync percentage;
- split logs;
- competitions.

Unsupported cards may render as:
- `Not tracked yet`
- `Requires enhanced stats sync`
- `Coming later`

### Clan Members

Target columns for frontend-only MVP:

```text
RSN | Clan Rank | Total | Last Sync | Status | Flags | Actions
```

Future columns, only if data exists:

```text
Combat | Build | Last Gain | Sync Status Reason
```

Rules:
- Do not fake account build.
- Do not fake combat level.
- Do not fake last gain.
- Existing `TotalLevel` may be shown as `Total`.

### Review Queues

Target tabs:
- Possible RSN Changes
- Missing Members
- Rank Reviews

Use existing admin queue cases.

Card fields:
- case title;
- player/RSN;
- evidence;
- detected/created date;
- current status;
- available actions.

Future optional fields:
- confidence percent;
- total XP delta;
- boss KC match;
- HiScores match;
- last snapshot.

If future fields are missing, hide the metric row.

### Player Profile

Use existing fields:
- username;
- current rank;
- eligible rank if available;
- status;
- last seen;
- last synced;
- latest snapshot values currently available;
- open cases;
- recent events.

Future sections:
- skill breakdown;
- total XP;
- combat level;
- boss KC;
- recent drops;
- rank history;
- admin notes.

For future sections, show unavailable states or hide them.

### Activity Log

Render lifecycle/clan-log data as a polished table.

Columns:
- time;
- event;
- member;
- details;
- status;
- action, if available.

Map known event groups to status tones.
Unknown events should still render safely.

## Accessibility

- Maintain readable contrast.
- Do not rely on color alone for status.
- Use visible focus states.
- Buttons need accessible labels.
- Tables need semantic headers.
- Avoid tiny unreadable text.

## Responsive behavior

Desktop is primary.

Minimum acceptable behavior:
- sidebar can stack/collapse at narrow widths;
- tables can horizontally scroll;
- cards wrap cleanly;
- no critical content clipped.

## Design implementation rules for Codex

- Prefer CSS variables and shared components.
- Do not inline one-off styles everywhere.
- Do not introduce a large UI library.
- Do not add external image/font dependencies without explicit approval.
- Keep existing routes and data loading behavior working.
- Do not change Core/Worker/API contracts for pure design work.
