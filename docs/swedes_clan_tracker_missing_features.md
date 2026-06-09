# OSRS Clan Hub UI Gap Analysis

Repository: `Schlebbe/SwedesClanTracker`  
Branch: `feature/ui-overhaul`  
Generated: `2026-06-09T11:16:23+00:00`

## Current implementation snapshot

- Frontend exists at `swedesclantracker-frontend` and currently mounts: `Dashboard`, `Members`, `Player Profile`, `Clan Log`, `Admin Queue`, and `Readiness`.
- Frontend app API calls currently target `/api/app/home`, `/api/app/admin-queue`, `/api/app/roster`, `/api/app/players/{id}/profile`, `/api/app/clan-log`, `/api/app/readiness`, and `/api/status`.
- Core player snapshots currently store only `TotalLevel`, `Ehb`, `Ehp`, `Collections`, and `PetCount`.
- Existing review/promotions endpoints already cover several mutations, but the new `App` API namespace mostly exposes read-only DTOs.

## Missing features and required backend support

### P0 — OSRS Clan Hub visual shell and navigation

**Feature ID:** `design-system-osrs-shell`

**Mockup intent:** Left nav titled 'Clan Hub Admin', clan name 'Iron Vanguard', OSRS-like dark stone panels, brass borders, beveled buttons, icon-based navigation, top HiScores sync bar, and admin profile area.

**Current state:** App shell exists, but labels are 'SwedesClanTracker' / 'Clan Tracker Console' and CSS uses generic modern dashboard tokens, rounded panels, Segoe UI/system fonts, and simple nav buttons.

**Frontend work:**
- Create AppShell, SidebarNav, TopStatusBar, BeveledButton, StonePanel, StatusPill, IconBadge components.
- Rename nav labels to Dashboard, Clan Members, Player Profiles, Name Changes, Rank Reviews, Activity Log, Settings, Support.
- Add top-level actions and consistent page titles.
- Replace generic table/card styles with OSRS-inspired stone/brass CSS tokens.

**Support needed from API:**
- Optional GET /api/app/shell or include shell metadata in /api/app/home: clanName, appTitle, currentUserDisplayName, userRole, unreadNotificationCount, lastSnapshotAt, syncState.

**Support needed from Core:**
- No schema change required for pure visual shell unless user/role metadata should come from DB instead of config.

**Support needed from Worker:**
- Continue reporting status heartbeat so top bar can derive sync state.

### P0 — Dashboard KPI cards: tracked members, weekly XP, boss KC, collection-log sync

**Feature ID:** `dashboard-osrs-kpis`

**Mockup intent:** Operational dashboard should show Tracked Members, Weekly XP Gained, Boss KC Logged, Collection Log Sync, Pending Admin Tasks, Quick Tools, and Recent Clan Activity.

**Current state:** DashboardSurface renders Tracker Health, Work Waiting Preview, Roster Posture, placeholder progression modules, and Recent Meaningful Clan Changes. It does not render weekly XP, boss KC, collection-log sync, split logs, or OSRS-specific KPI cards.

**Frontend work:**
- Replace placeholder progression section with KPI cards matching the mockups.
- Add PendingAdminTasks cards for RSN changes, stale members, and rank reviews.
- Add QuickTools panel with Add Member, Run Audit, Sync HiScores, Clear Temp Cache.
- Render Recent Clan Activity as a table with event, member, details, status, and action columns.

**Support needed from API:**
- Extend GET /api/app/home with dashboardKpis: trackedMembers, totalMembers, weeklyXpGained, bossKcLogged, collectionLogItemsSynced, syncAccuracy.
- Extend GET /api/app/home with pendingTasks: possibleRsnChanges, staleMembers, rankReviews, missingMembers.
- Add GET /api/app/activity/recent or extend /api/app/clan-log with table-ready rows: time, eventType, member, details, status, actionTarget.
- Add POST endpoints for quick tools: /api/app/roster/sync, /api/app/audit/run, /api/app/cache/clear, /api/app/members/add.

**Support needed from Core:**
- Add TotalXp to PlayerSnapshot or a richer PlayerStatSnapshot table.
- Add BossKcSnapshot or boss KC dictionary table if boss KC should be tracked historically.
- Add CollectionLogSnapshot with item count and optionally per-item detail.
- Add SyncRun / SyncAudit records for success counts, failure counts, API call counts, and last run metadata.

**Support needed from Worker:**
- Collect and persist total XP from Temple/WiseOldMan/HiScores, not only total level/EHP/EHB.
- Collect boss KC totals from Temple player_stats.php?bosses=1 or another API source and persist deltas.
- Persist collection-log sync outcome, item count, and freshness.
- Emit lifecycle events for sync completed, boss KC change, collection-log update, audit run, and quick-tool actions.

### P0 — Clan Members roster with combat, total, build, richer statuses, and pagination

**Feature ID:** `members-rich-roster`

**Mockup intent:** Roster table columns should be RSN, Clan Rank, Combat, Total, Last Gain, Sync Status, Actions. Rows should include Ironman/HCIM/Main/Skiller build tags and warning statuses like Stale, Possible RSN Change, Not on HiScores.

**Current state:** MembersSurface renders Username, Rank, Status, Last sync, Flags, Profile. It filters only by username and status. /api/app/roster returns rank/status/lastSync/lastSeen and flags, but no combat, total snapshot, account build, last gain, sync reason, page metadata, or row actions.

**Frontend work:**
- Change table columns to RSN, Clan Rank, Combat, Total, Last Gain, Sync Status, Actions.
- Add filter controls for rank, build/account type, status, and sorting.
- Add pagination or virtualized table if clan size grows.
- Add row action menu with Open Profile, Sync Now, Mark Review, Edit Rank/Notes where supported.

**Support needed from API:**
- Extend GET /api/app/roster query params: search, rank, build, status, sort, page, pageSize.
- Return page metadata: totalRows, page, pageSize, totalPages.
- Return row fields: accountBuild, combatLevel, totalLevel, totalXp, lastGainAt, lastGainAmount, syncStatus, syncStatusReason, latestSnapshotAt.
- Add POST /api/app/players/{id}/sync-priority for row-level Sync Now.
- Add POST /api/app/players/{id}/mark-review or expose existing review status update through app-facing API.

**Support needed from Core:**
- Add AccountBuild/AccountType and CombatLevel to Player or latest snapshot.
- Add TotalXp and last gain calculation support.
- Store last sync failure reason / not-on-hiscores status separately from player lifecycle status.

**Support needed from Worker:**
- Infer or fetch account build type from Temple/WiseOldMan data where possible.
- Persist last successful gain/delta by comparing current and previous snapshots.
- Classify sync result: synced, stale, not_on_hiscores, possible_rsn_change, missing_pending_review.

### P1 — Rich OSRS player profile: XP, combat, skills, rank history, recent drops, notes

**Feature ID:** `player-profile-rich-osrs`

**Mockup intent:** Profile should show avatar/emblem, Active chip, General rank, joined clan date, verified Ironman, Total XP, Total Level, Combat Level, skill breakdown, rank history timeline, admin notes, and recent activity/drops.

**Current state:** PlayerProfileSurface currently shows Current State, Open Cases, Recent Player Events, and placeholder panels for rank/stat history. /api/app/players/{id}/profile explicitly reports history availability as false. There is no admin notes model, skill breakdown, total XP, combat level, or recent drops model.

**Frontend work:**
- Replace Current State list with profile header and OSRS stat cards.
- Add SkillBreakdown grid for important skills and a link to all skills.
- Render RankHistoryTimeline when API returns entries.
- Add AdminNotes editor with save state.
- Add RecentActivity/RecentDrops table.

**Support needed from API:**
- Extend GET /api/app/players/{id}/profile with totalXp, totalLevel, combatLevel, accountBuild, clanJoinDate/firstSeenAt, verifiedSource, latestSnapshot, skillSummary, rankHistory, recentDrops, recentBossKc, adminNotes.
- Add GET /api/app/players/{id}/skills for all skills if large.
- Add GET /api/app/players/{id}/rank-history.
- Add GET/PUT /api/app/players/{id}/admin-notes.
- Add GET /api/app/players/{id}/activity?type=drops|levels|bosses|all.

**Support needed from Core:**
- Add PlayerAdminNote entity with PlayerId, Body, UpdatedAt, UpdatedBy.
- Add RankHistory or derive durable rank events from PromotionCandidate approvals and status lifecycle events.
- Add SkillSnapshot table: playerId, timestamp, skillName, level, xp, rank.
- Add Drop/Split/RecentActivity entity if drops/splits are user-entered or Discord-ingested.

**Support needed from Worker:**
- Capture skill levels/XP from a source that provides per-skill data.
- Emit rank-history lifecycle events with oldRank, newRank, approvedBy, reason.
- Ingest drops/splits only if there is a source: manual web form, Discord bot command, or external tracker integration.

### P0 — Possible RSN Changes review screen with confidence metrics and actions

**Feature ID:** `rsn-change-review-cards`

**Mockup intent:** Review queue tab should show Possible RSN Changes with confidence cards: old RSN, suspected new RSN, XP delta, boss KC match, HiScores match, last snapshot, and actions Link RSN / Reject / Ask Member.

**Current state:** AdminQueueSurface is a generic lane-based queue. SyncService can auto-suggest merges using TotalLevel/EHB/EHP deltas and writes MERGE_SUGGESTED/MERGE_ACTION_REQUIRED metadata, but the app-facing queue exposes only generic evidence strings and alternatives. Existing ReviewController has merge confirm/reassign/manual/abort endpoints, but AdminQueueSurface does not call mutation endpoints.

**Frontend work:**
- Add ReviewQueuesSurface with tabs: Possible RSN Changes, Missing Members, Rank Reviews.
- Render RSNMatchCard using structured candidate comparison fields rather than plain evidence strings.
- Wire Link RSN, Reject, Manual/Reassign, and Ask Member buttons to API mutations.
- After mutation, refresh queue and activity log.

**Support needed from API:**
- Add GET /api/app/rsn-changes or extend /api/app/admin-queue with structured caseType='rsn_change' fields.
- Return oldPlayer, newPlayer, totalLevelDelta, totalXpDelta, ehpDelta, ehbDelta, bossKcMatchPercent, hiscoresMatchPercent, confidencePercent, confidenceLabel, candidatePreviousPlayers, detectedAt, lastSnapshotAt.
- Expose app-facing mutations: POST /api/app/rsn-changes/{caseId}/link, /reject, /ask-member, /reassign.
- Optionally wrap existing /api/review/players/{id}/merge/* endpoints so the frontend uses one app API namespace.

**Support needed from Core:**
- Create RenameReviewCase or structured metadata DTO instead of storing only JSON blobs in LifecycleEvent.
- Persist confidence score and comparison components for repeatable UI display.
- Enhance merge detection to include total XP and boss KC comparison once those snapshots exist.

**Support needed from Worker:**
- Continue detecting possible renames, but include richer matching signals: total XP, total level, EHP/EHB, boss KC, collection count/pets, and source freshness.
- Optional: integrate a name-history source if reliable.

### P1 — Dedicated Rank Reviews screen

**Feature ID:** `rank-reviews-screen`

**Mockup intent:** Rank Reviews should be a first-class queue separate from RSN changes and missing players, with promotion/demotion details and approve/dismiss actions.

**Current state:** Promotion candidates exist, /api/promotions supports pending list, approve, dismiss, and approve-all. The new app surface merges promotions into Admin Queue lanes. There is no dedicated Rank Reviews tab/page matching the mockups.

**Frontend work:**
- Add RankReviewsSurface or ReviewQueues tab.
- Render candidate cards with current rank, eligible rank, reason, WOM role/candidate type, age, and action buttons.
- Wire approve/dismiss/mark rename suspect actions.

**Support needed from API:**
- Either use existing GET /api/promotions and POST /api/promotions/{id}/approve|dismiss, or add app namespace equivalents.
- Return richer frontend fields: candidateType, currentWomRole, risk, confidence, supporting snapshot values, createdAt/timeAgo.
- Add POST mark-rename-suspect if only available through Discord workflow.

**Support needed from Core:**
- PromotionCandidate already exists; may need fields for reviewedBy, reviewedAt, decisionNote, source.

**Support needed from Worker:**
- Already creates promotion candidates from rank rules; add lifecycle metadata for why the candidate exists and whether WOM already reflects the role.

### P1 — Activity Log as Recent Clan Activity table

**Feature ID:** `activity-log-polished`

**Mockup intent:** Use a compact table with Timestamp, Event Type, Member Details, Status, and Action, including statuses like Success, General, Updated, Retrying, Completed.

**Current state:** ActivityController provides an activity feed from LifecycleEvents and AppController /clan-log provides important/routine summaries, but the current frontend ClanLogSurface was not fully inspected here and dashboard only shows 'Recent Meaningful Clan Changes' as a list.

**Frontend work:**
- Create RecentClanActivityTable component usable on dashboard and Activity Log page.
- Map lifecycle event groups to icons and tone chips.
- Add filtering by group and time window.

**Support needed from API:**
- Return normalized fields: timestamp, eventType, title, member, details, statusTone, statusLabel, actionLabel, actionHref/actionCaseId.
- Support query params: take, group, playerId, since, includeRoutine.

**Support needed from Core:**
- No schema change if LifecycleEvent remains the canonical source.
- Optional: standardize lifecycle event metadata keys so UI projections are simpler.

**Support needed from Worker:**
- Ensure worker emits structured lifecycle events for boss KC, drops/splits, collection log, sync successes/failures, and manual admin actions.

### P2 — Drops, splits, boss KC, and competitions

**Feature ID:** `drops-splits-competitions`

**Mockup intent:** Dashboard/profile mockups include split logged, boss KC logged, recent drops, PvM competition started, and collection-log details.

**Current state:** Repo has pet hiscore Discord events and collection count/pet count snapshots, but no general drops/splits/competition domain model was found. Boss KC is requested from Temple with bosses=1 but not persisted in PlayerSnapshot.

**Frontend work:**
- Add dashboard rows and profile recent activity for drops/splits/boss KC.
- Add optional Competitions page or dashboard widget.
- Add forms/actions for manual split logging if Discord integration is not the source.

**Support needed from API:**
- Add GET /api/app/drops/recent, POST /api/app/drops, GET /api/app/competitions, POST /api/app/competitions.
- Extend profile endpoint with recentDrops and bossKcSummary.
- Extend dashboard home with weeklyBossKc and recentSplits.

**Support needed from Core:**
- Add BossSnapshot/BossKillCount entity for per-boss KC over time.
- Add DropLogEntry/SplitLogEntry entities.
- Add Competition entity if competitions are managed locally.

**Support needed from Worker:**
- Parse/store boss KC values from Temple stats response.
- Ingest drops/splits from manual API, Discord bot commands, or another source.
- Compute weekly deltas from snapshots.

### P1 — Top/quick actions: Export CSV, Add Member, Update Roster, Run Audit, Clear Temp Cache

**Feature ID:** `export-add-member-run-audit-cache`

**Mockup intent:** Buttons in mockups should perform real admin operations rather than being decorative.

**Current state:** Existing endpoints can run one sync and set manual pets; review/promotions mutations exist. No app-level export CSV, add member, audit report, or cache clear endpoint was identified. The frontend AppDataApi currently only has login/logout and GET calls.

**Frontend work:**
- Add action handlers to dashboard and members page.
- Add confirmation modals for destructive/high-cost actions.
- Show success/error toasts and refresh affected surfaces.

**Support needed from API:**
- GET /api/app/roster/export.csv
- POST /api/app/members with RSN and optional rank/build/notes.
- POST /api/app/roster/sync or reuse /api/sync/run-once behind app namespace.
- POST /api/app/audit/run returning audit id/status.
- POST /api/app/cache/clear if an actual cache exists; otherwise omit button.

**Support needed from Core:**
- Optional AuditRun entity with results and createdBy/createdAt.
- Optional ManualMemberAdd lifecycle event.

**Support needed from Worker:**
- Consume priority sync/audit requests through LifecycleEvents or a dedicated queue.
- Report audit progress via /api/status or an audit endpoint.

### P2 — Notifications, current user, and admin role display

**Feature ID:** `notifications-and-admin-user`

**Mockup intent:** Top-right notification bell, shield icon, current admin name, role, and unread count.

**Current state:** Cookie auth and login flow exist, but the frontend hardcodes no current-user display beyond unauthenticated login. App shell does not fetch /me or notification counts.

**Frontend work:**
- Add current user card and notification bell to TopStatusBar.
- Add dropdown for sign out, session info, and settings link.

**Support needed from API:**
- GET /api/auth/me returning username, displayName, roles, permissions.
- GET /api/app/notifications/summary returning unread count and latest critical alerts.
- POST /api/auth/logout is already present in the frontend data API but should be wired in UI.

**Support needed from Core:**
- Only needed if notifications are persisted; otherwise compute from open lifecycle events.

**Support needed from Worker:**
- Emit critical lifecycle events with severity so notification summary can derive alerts.

## Recommended implementation order

### Phase 1: Make the existing UI match the OSRS shell without changing backend
- `design-system-osrs-shell`
- `activity-log-polished basic styling`
- `members-rich-roster frontend layout using existing fields with placeholders`

### Phase 2: Add app-facing API DTOs for the mockups
- `dashboard-osrs-kpis partial: tracked/stale/reviews from existing DB`
- `rsn-change-review-cards structured DTOs from existing merge metadata`
- `rank-reviews-screen using existing PromotionCandidate endpoints`

### Phase 3: Add new persisted OSRS data
- `TotalXp and CombatLevel snapshots`
- `SkillSnapshot table`
- `BossKcSnapshot table`
- `AdminNotes and RankHistory`

### Phase 4: Optional community-clan features
- `drops-splits-competitions`
- `notifications-and-admin-user`

## Source evidence

- `README.md` lines 7-11: Repo is full-stack with API, Worker, Core, and React/Vite frontend.
- `swedesclantracker-frontend/src/App.jsx` lines 5-14, 119-230: Frontend currently mounts Dashboard, Members, Player Profile, Clan Log, Admin Queue, and Readiness surfaces.
- `swedesclantracker-frontend/src/data/appDataApi.js` lines 5-41: Frontend API client currently calls app home, roster, admin queue, player profile, clan log, readiness, and live status.
- `SwedesClanTracker.Api/Controllers/AppController.cs` lines 17-24, 132-140, 262-312, 53-69, 201-226, 3-32: AppController exposes home/admin queue/roster/profile/readiness DTOs, but profile history is marked unavailable.
- `SwedesClanTracker.Core/Domain.cs` lines 25-35: Core PlayerSnapshot currently stores TotalLevel, EHB, EHP, Collections, and PetCount only.
- `SwedesClanTracker.Core/TempleClient.cs` lines 8-15, 34-45, 48-76: TempleClient currently returns TotalLevel, EHB, EHP, Collections, and pets, but not total XP, skill breakdown, combat level, or boss KC persistence.
- `SwedesClanTracker.Core/SyncService.cs` lines 50-96, 101-145: SyncService creates snapshots and auto-suggests merges using TotalLevel/EHB/EHP deltas.
- `SwedesClanTracker.Api/Controllers/ReviewController.cs` lines 82-110, 113-157, 160-214, 240-274: Existing ReviewController has merge/missing review action endpoints that can be reused or wrapped.
- `SwedesClanTracker.Api/Controllers/PromotionsController.cs` lines 16-44, 46-75: Existing PromotionsController has pending promotions and approve/dismiss endpoints.
