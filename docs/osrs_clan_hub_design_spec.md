# Clan Hub Admin — Codex Implementation Spec

This file translates the generated UI images into a component-oriented design spec that Codex can implement.

## Goal

Build an **unofficial Old School RuneScape-inspired clan management/admin panel** for **Iron Vanguard**. The style should feel like a dark medieval OSRS clan tracker: stone panels, brass borders, muted gold text, green sync/status highlights, compact data tables, and beveled buttons.

Avoid official Jagex/OSRS logos or copyrighted sprites. Use custom fantasy icons or generic icon libraries.

## Design Tokens

```json
{
  "colors": {
    "background": "#0b0d0e",
    "surface": "#121416",
    "surfaceRaised": "#181b1d",
    "surfaceInset": "#0f1112",
    "border": "#3c3324",
    "borderStrong": "#7b6230",
    "gold": "#e0bd58",
    "goldMuted": "#b89445",
    "text": "#e7dfcf",
    "textMuted": "#a99f8b",
    "green": "#7bd878",
    "greenDark": "#17361d",
    "warning": "#e0a83e",
    "danger": "#e06c5f",
    "blue": "#6fa1d8",
    "purple": "#a983e8"
  },
  "typography": {
    "fontFamilyDisplay": "serif or fantasy-flavoured display font",
    "fontFamilyUi": "system-ui, Inter, Segoe UI, sans-serif",
    "fontFamilyMonoAccent": "monospace for IDs, times, and compact table metadata",
    "headingWeight": 700,
    "bodyWeight": 400,
    "letterSpacingSmallCaps": "0.08em"
  },
  "layout": {
    "appWidth": "desktop-fluid",
    "sidebarWidth": 224,
    "topBarHeight": 72,
    "contentPadding": 28,
    "panelRadius": 4,
    "cardGap": 16,
    "borderWidth": 1
  },
  "effects": {
    "panelShadow": "0 8px 28px rgba(0,0,0,0.35)",
    "goldGlow": "0 0 0 1px rgba(224,189,88,0.35)",
    "greenGlow": "0 0 12px rgba(123,216,120,0.22)",
    "texture": "subtle dark stone/noise background"
  }
}
```

## Shared App Shell

Every screen uses the same shell:

- `Sidebar`
- `TopBar`
- `MainContent`
- `Footer`

### Sidebar

Brand:

- `Clan Hub Admin`
- `Iron Vanguard`
- custom shield / crossed-swords crest

Navigation:

1. Dashboard
2. Clan Members
3. Player Profiles
4. Name Changes
5. Rank Reviews
6. Activity Log

Lower sidebar:

- `+ New Event`
- Settings
- Support

### TopBar

Elements:

- `HiScores Sync: Stable`
- `Last Snapshot: 2m ago`
- global search: `Search members or hiscores...`
- notification icon
- shield icon
- admin menu: `ZezimaAdmin`, `Clan Leader`

## Screen 1 — Operational Dashboard

Route: `/dashboard`

Active nav: `Dashboard`

Main actions:

- `Export Roster`
- `Update Roster`

KPI cards:

| Title | Value | Subtext / trend |
|---|---:|---|
| Tracked Members | 470 / 482 | 97.5% tracked, +7 this week |
| Weekly XP Gained | 184.2M | +32.6M vs last week |
| Boss KC Logged | 3,421 | +512 vs last week |
| Collection Log Sync | 118 | 99.1% accuracy |

Pending admin tasks:

| Task | Count | Description | Action |
|---|---:|---|---|
| Possible RSN Changes | 5 | Members with names that may have changed. | Review |
| Stale Members | 12 | Members inactive for 30+ days. | Review |
| Rank Reviews | 8 | Promotions or demotions awaiting approval. | Review |

Quick tools:

- Add Member
- Run Audit
- Sync HiScores
- Clear Temp Cache

Recent clan activity table:

| Time | Event | Member | Details | Status | Admin |
|---|---|---|---|---|---|
| 14:22:15 | New Recruit | Iron_Zezima22 | Joined the clan | SUCCESS | View |
| 14:18:02 | Rank Promotion | Vanguard_Slayer | Promoted to Captain | GENERAL | View |
| 14:05:44 | Manual Sync | HCIM_BTW_99 | HiScores updated | UPDATED | View |
| 13:58:12 | Split Logged | ToB Raiders | Team split: 5 players | SUCCESS | View |
| 13:45:33 | Competition Started | PvM Comp #12 | 5v5 Boss League | ACTIVE | View |
| 13:12:09 | Name Change Detected | NoobBuster1 | Possible name change | PENDING | Review |
| 12:41:51 | Rank Demotion | LootGoblinV2 | Demoted to Member | GENERAL | View |
| 12:15:27 | Boss KC Logged | Iron_King_77 | Corporeal Beast x12 | SUCCESS | View |

## Screen 2 — Clan Members

Route: `/members`

Active nav: `Clan Members`

Main actions:

- `Export CSV`
- `Add Member`

Filters:

- Search by RSN
- All Ranks
- All Builds
- Last Gain
- Sort by: Last Gain

Table columns:

`RSN`, `Clan Rank`, `Combat`, `Total`, `Last Gain`, `Sync Status`, `Actions`

Rows:

| RSN | Build | Rank | Combat | Total | Last Gain | Status |
|---|---|---|---:|---:|---|---|
| Iron_Slayer99 | Ironman | General | 126 | 2,277 | 2h ago | Synced |
| LootGoblinV2 | Ironman | Captain | 115 | 2,125 | 14m ago | Synced |
| PvMasterX | HCIM | Leader | 124 | 2,196 | Just now | Synced |
| Old_Hag_42 | Ironman | Recruit | 98 | 1,487 | 42d ago | Stale |
| HerbFarmer_265 | Ironman | Corporal | 121 | 2,062 | 19h ago | Synced |
| TeaBagger_235 | Ironman | Captain | 101 | 1,806 | 14h ago | Synced |
| ZulrahSlayer_938 | Ironman | Recruit | 117 | 2,001 | 6h ago | Synced |
| SkulledOut_702 | Main | Sergeant | 110 | 1,934 | 12h ago | Possible RSN Change |
| Dungeoneer_885 | Skiller | General | 104 | 1,721 | 11h ago | Not on HiScores |
| ClueSeeker_33 | Ironman | Captain | 112 | 1,964 | 9h ago | Synced |

Footer:

- Total Members: 482
- Tracked: 470
- Stale: 12
- Page 1 of 24

## Screen 3 — Review Queues / Possible RSN Changes

Route: `/reviews/name-changes`

Active nav: `Review Queues`

Tabs:

- Possible RSN Changes
- Missing Members
- Rank Reviews

KPI cards:

| Title | Value | Detail |
|---|---:|---|
| Possible RSN Changes | 12 | +4 since yesterday |
| Auto-Linked | 142 | This month |
| Confidence Score | 98.4% | High Reliability |

Review cards:

### Iron_Slayer88 → GoldenAxe_Main

- Confidence: High
- Detection date: 2023-10-24 14:22
- Match: 98%
- Old total level: 2,276
- Old total XP: 184.2M
- New total level: 2,301
- New total XP: 326.4M
- Signals:
  - Total XP Delta: +142,204
  - Boss KC Match: 100%
  - HiScores Match: 99.2%
  - Last Snapshot: 2m ago
- Actions:
  - Link RSN
  - Reject
  - Ask Member

### HC_DeadAgain → Reggie_Iron

- Confidence: Medium
- Detection date: 2023-10-24 11:05
- Match: 74%
- Signals:
  - Total XP Delta: +2.1M
  - Boss KC Match: Diff -4.2
  - HiScores Match: 74.0%
  - Last Snapshot: 15m ago

### NoobBuster1 → LootGoblinV2

- Confidence: High
- Detection date: 2023-10-23 23:59
- Match: 99%
- Signals:
  - Total XP Delta: +8,440
  - Boss KC Match: 100%
  - HiScores Match: 99.8%
  - Last Snapshot: 1m ago

Footer:

- Showing 3 of 12 possible matches
- Previous / Next controls

## Screen 4 — Player Profile

Route: `/players/:rsn`

Active nav: `Player Profiles`

Profile:

- RSN: `Iron_King77`
- Rank: `General`
- Status: `Active`
- Joined clan: `Aug 12, 2021`
- Verified Ironman

Actions:

- Update HiScores
- Promotion Review

Stat cards:

| Title | Value | Detail |
|---|---:|---|
| Total XP | 412,903,115 | +1.2M this week |
| Total Level | 2,231 | Maxed Account |
| Combat Level | 126 | Quest Points: 297 |

Skill breakdown:

| Skill | Level |
|---|---:|
| Attack | 99 |
| Strength | 99 |
| Slayer | 99 |
| Runecraft | 99 |
| Prayer | 99 |
| Agility | 92 |

Rank history:

1. Promoted to General — Oct 14, 2023  
   Approved by Council following 500M clan XP milestone.
2. Promoted to Captain — Jan 02, 2023  
   Met participation requirements for 3 consecutive months.
3. Joined as Recruit — Aug 12, 2021  
   Introductory trial period started.

Recent activity:

| Event | Value | Date |
|---|---:|---|
| Loot: Scythe of Vitur (Split) | 351M | 2h ago |
| Theatre of Blood KC | 500 | 5h ago |
| 99 Runecraft Achieved | Level 99 | 1d ago |
| Clue Scroll Completed (Elite) | 3rd Age Platelegs | 2d ago |
| Bandos KC | 1,287 | 3d ago |

## Suggested React File Map

```text
src/components/layout/AppShell.tsx
src/components/layout/Sidebar.tsx
src/components/layout/TopBar.tsx
src/components/ui/StatCard.tsx
src/components/ui/Panel.tsx
src/components/ui/Button.tsx
src/components/ui/DataTable.tsx
src/components/ui/StatusChip.tsx
src/components/reviews/ReviewCard.tsx
src/components/profile/Timeline.tsx
src/components/profile/SkillTile.tsx
src/pages/DashboardPage.tsx
src/pages/ClanMembersPage.tsx
src/pages/ReviewQueuesPage.tsx
src/pages/PlayerProfilePage.tsx
```

## Implementation Guidance for Codex

Use the JSON file for exact component/data structure. Use this Markdown file as the readable product/design brief.

Recommended first prompt for Codex:

> Implement this design spec as a React + TypeScript app. Create reusable components for the shared shell, cards, tables, status chips, review cards, timeline, and skill tiles. Use CSS variables for the design tokens. Keep the UI responsive but prioritize desktop layout fidelity.
