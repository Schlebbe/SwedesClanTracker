# Scope Review: OSRS Clan Hub UI Features

Repository: `Schlebbe/SwedesClanTracker`  
Branch: `feature/ui-overhaul`  
Generated: `2026-06-09T11:26:52+00:00`

This document classifies the OSRS Clan Hub Admin mockup features into three buckets:

1. **Likely in scope for the UI overhaul** — can be built mostly with frontend work or light DTO changes.
2. **Heavy rewrite** — requires new persisted domain models, richer worker collection, and app-facing APIs.
3. **Likely out of scope** — expands the tracker into a different product area or depends on unavailable data/integrations.

## Recommended scope cut

### Include in `feature/ui-overhaul`
- OSRS visual shell and component design system
- Dashboard restyle using currently derivable stats
- Roster restyle with existing fields plus graceful placeholders
- Admin queue card layout using existing evidence
- Rank review UI using existing promotion endpoints
- Activity log polish

### Defer to backend/domain milestones
- Total XP/combat/per-skill/boss KC collection
- Structured RSN confidence engine
- Player rank history and admin notes
- Dashboard weekly XP/KC aggregates
- Async job queue for full sync/audit actions

### Exclude for now
- Drops/split accounting
- Competitions/leagues
- Messaging workflow for Ask Member
- Full collection-log item sync
- Notification center/RBAC
- Clear temp cache unless real cache exists

## Likely in scope for MVP

### OSRS visual shell and navigation

**Why:** Mostly frontend CSS/component work. Existing surfaces already exist and can be relabeled/restyled.

**Recommended scope:** Implement app shell, sidebar labels, top sync bar, OSRS-style cards, table styling, and basic icons using safe custom assets.

**Backend need:** Optional shell metadata only; can start with existing /api/app/home and /api/status.

### Roster restyle using existing data

**Why:** The Members surface and /api/app/roster already exist.

**Recommended scope:** Ship the OSRS table layout first with current fields, then add combat/total/build once backend supports them.

**Backend need:** No rewrite for visual phase; DTO extension later.

### Admin queue restyle using existing generic cases

**Why:** Existing admin queue and review endpoints can be reused visually.

**Recommended scope:** Render current merge/missing cases as cards, but mark advanced confidence metrics as future backend work.

**Backend need:** Potential app-facing wrappers for existing review actions.

### Rank review UI from existing PromotionCandidate workflow

**Why:** Promotion endpoints already exist.

**Recommended scope:** Create a dedicated tab/page backed by existing /api/promotions endpoints before inventing a new model.

**Backend need:** Light DTO improvements for reason, risk, and supporting evidence.

### Activity log polish

**Why:** LifecycleEvent data already exists and can be projected into a nicer table.

**Recommended scope:** Normalize display mapping in frontend first; only later standardize event metadata.

**Backend need:** Optional normalized /api/app/activity/recent DTO.

## Features that would take a heavy rewrite

### Rich OSRS stats model

**Mockup examples:** Total XP, Combat Level, Skill Breakdown, Boss KC Logged, Weekly XP Gained

**Why this is heavy:** Current PlayerSnapshot stores only TotalLevel, EHB, EHP, Collections, and PetCount. The mockups need per-skill snapshots, total XP, combat level, boss KC, and historical deltas.

**Rewrite risk:** High

**Worker support needed:**
- Collect total XP, combat level, per-skill levels/XP, and boss KC from a reliable source.
- Persist historical snapshots often enough to compute weekly deltas.
- Classify stale/not-found/synced states from sync outcomes.

**Core support needed:**
- Add SkillSnapshot or PlayerSkillSnapshot table.
- Add TotalXp and CombatLevel to snapshots or latest-player projection.
- Add BossKcSnapshot for per-boss historical values.
- Add sync/audit run records for freshness and failure reasons.

**API support needed:**
- Extend roster/profile/home DTOs with total XP, combat, skill summaries, boss KC, last gain, sync reason.
- Support time-window aggregation for weekly XP and boss KC.

**Recommendation:** Do not block UI overhaul on this. Add DTO placeholders or feature flags, then implement stats collection as a separate backend milestone.

### Structured RSN-change confidence engine

**Mockup examples:** 98% Match, Total XP Delta, Boss KC Match, HiScores Match, Link RSN

**Why this is heavy:** Current merge suggestion appears to use simple snapshot deltas and generic metadata. The mockup requires repeatable confidence scoring and structured comparison components.

**Rewrite risk:** High

**Worker support needed:**
- Calculate confidence using multiple signals: total XP, total level, EHP/EHB, boss KC, collection count, pets, and source freshness.
- Persist candidate comparisons rather than only emitting generic lifecycle events.

**Core support needed:**
- Add RenameReviewCase / RsnChangeCandidate entity or structured metadata schema.
- Persist confidence score, score breakdown, old/new player references, and decision status.

**API support needed:**
- Add structured GET /api/app/rsn-changes.
- Add POST link/reject/reassign/ask-member endpoints or app-facing wrappers around existing review endpoints.

**Recommendation:** Implement a basic card UI using current evidence first. Treat advanced confidence scoring as a dedicated backend feature.

### Dashboard KPI aggregation

**Mockup examples:** Weekly XP Gained, Boss KC Logged, Collection Log Sync, Pending Admin Tasks

**Why this is heavy:** Some counts can be derived now, but XP/KC/log accuracy requires richer historical snapshots and standardized worker events.

**Rewrite risk:** Medium-High

**Worker support needed:**
- Emit structured sync run events.
- Persist historical stat deltas and collection-log freshness.
- Optionally track API success/failure metrics.

**Core support needed:**
- Add stat history models and SyncRun/AuditRun models.
- Create aggregate query support for weekly windows.

**API support needed:**
- Return dashboardKpis and pendingTasks from /api/app/home.
- Return status freshness and confidence/accuracy metadata.

**Recommendation:** Start with KPIs derivable from existing data: tracked members, stale members, pending reviews, last sync. Add XP/KC cards once snapshots exist.

### Player profile timeline, notes, and rich recent activity

**Mockup examples:** Rank History, Admin Notes, Recent Activity, Recent Drops

**Why this is heavy:** Profile history is currently marked unavailable. Admin notes and rich drops are not present as first-class models.

**Rewrite risk:** Medium-High

**Worker support needed:**
- Emit durable rank-change and relevant player activity events.
- Ingest drops only if a source exists.

**Core support needed:**
- Add PlayerAdminNote entity.
- Add RankHistory or durable rank lifecycle projection.
- Add DropLog/SplitLog only if product scope includes drops.

**API support needed:**
- Add GET/PUT admin notes endpoint.
- Add rank history and player activity endpoints.
- Extend player profile DTO.

**Recommendation:** Admin notes are a reasonable isolated addition. Drops/splits should not be bundled into the first profile rewrite.

### Action workflows and worker job queue

**Mockup examples:** Update Roster, Run Audit, Sync HiScores, Clear Temp Cache, Add Member

**Why this is heavy:** Buttons require reliable mutation endpoints, background job orchestration, progress reporting, idempotency, auth, and error handling.

**Rewrite risk:** Medium

**Worker support needed:**
- Accept priority sync/audit jobs.
- Report job progress and completion.
- Avoid duplicate/high-cost external API calls.

**Core support needed:**
- Add Job/AuditRun/SyncRun records if actions are asynchronous.
- Record who triggered actions and their result.

**API support needed:**
- Add mutation endpoints for sync, audit, add member, export, and cache clear only where real behavior exists.
- Return job IDs and polling endpoints for long-running actions.

**Recommendation:** Keep non-existing quick tools hidden or disabled. Wire only actions that already exist or are cheap to implement.

## Features that are likely out of scope

### Drops and split accounting

**Reason:** This turns the tracker into a PvM loot ledger. It requires manual entry, Discord bot ingestion, or another external source, plus money/value normalization.

**Frontend impact:** Recent drops/splits can be mocked visually, but real tables/forms would be a separate product module.

**Backend impact:** Requires DropLogEntry/SplitLogEntry entities, APIs, validation, audit trail, and possibly item price data.

**Recommendation:** Out of scope for the UI overhaul. Revisit as a separate PvM module.

### Competitions / PvM leagues

**Reason:** Competitions require rules, participants, scoring windows, leaderboards, start/end states, and anti-abuse rules.

**Frontend impact:** Dashboard can show a read-only 'active competition' placeholder later.

**Backend impact:** Requires Competition, CompetitionParticipant, ScoreSnapshot, and leaderboard APIs.

**Recommendation:** Out of scope unless competitions are already a core project goal.

### Full item-level collection log sync

**Reason:** Current data appears to store collection count, not individual unlocked items. Item-level logs need a reliable source and much larger storage/projection surface.

**Frontend impact:** Keep 'Collection Log Sync' as a high-level count/health KPI.

**Backend impact:** Requires per-item collection-log schema, sync source, and potentially item metadata.

**Recommendation:** Keep count-level only for now.

### Messaging workflow for 'Ask Member'

**Reason:** The app has no clear messaging channel. Asking a member would require Discord, email, in-app notifications, or another communication integration.

**Frontend impact:** Button can be hidden, disabled, or converted to 'Copy message'.

**Backend impact:** Requires member contact mapping, message dispatch, delivery status, and privacy considerations.

**Recommendation:** Out of scope unless Discord integration is already planned.

### Multi-user RBAC and notification center

**Reason:** Useful, but not central to rendering the OSRS UI and could distract from tracker/domain work.

**Frontend impact:** Top-right admin display can be static or sourced from existing auth session.

**Backend impact:** Requires user/role/permission model and notification persistence.

**Recommendation:** Keep basic auth/logout. Defer notification center and fine-grained permissions.

### Clear Temp Cache quick tool

**Reason:** This is only meaningful if the app has a real cache that admins should clear. Otherwise it is cargo-cult admin UI.

**Frontend impact:** Remove or hide the button.

**Backend impact:** Should not add fake endpoint. Add only if a real cache and operational need exist.

**Recommendation:** Out of scope unless current infrastructure has a cache layer.

### Official-looking OSRS art/assets

**Reason:** The app should remain an unofficial tracker and avoid depending on official/copyrighted game UI art.

**Frontend impact:** Use custom OSRS-inspired icons, CSS borders, and color language instead.

**Backend impact:** None.

**Recommendation:** Out of scope. Keep the design inspired but original.

## Decision matrix

| Area | Classification | Ship in UI overhaul? | Worker | Core schema | API |
|---|---:|---:|---:|---:|---:|
| Visual OSRS restyle | In scope | Yes | False | False | Optional |
| Roster layout and filters | Partially in scope | Yes | Later for richer fields | Later for combat/build/XP | Recommended |
| Rank review UI | In scope | Yes | Minimal | Minimal | Light wrappers/DTOs |
| Advanced RSN confidence scoring | Heavy rewrite | No | True | True | True |
| Total XP/combat/skills/boss KC | Heavy rewrite | No | True | True | True |
| Drops/splits | Likely out of scope | No | Depends on source | True | True |
| Competitions | Likely out of scope | No | True | True | True |

## Practical guidance

The safest path is to treat `feature/ui-overhaul` as a visual and DTO-shaping milestone, not a domain rewrite. Build the OSRS shell, restyle the existing pages, and expose only the actions that already have real backend behavior. Any feature that depends on total XP, combat level, per-skill history, boss KC, drops, split logs, or competitions should become a separate backend milestone with explicit worker/core/API acceptance criteria.
