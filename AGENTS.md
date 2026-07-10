## Codex Pi Access

* Use `scripts/windows/pi/test-pi-ssh.ps1` to verify SSH connectivity from the local Codex environment.
* Use `scripts/windows/pi/check-pi-db-readonly.ps1` as the default one-command Pi SSH + read-only DB connectivity check for new chats.
* Never commit private keys, passwords, tokens, or real connection strings.

## Pi diagnostics guardrails

* Prefer the repository Pi scripts and `scripts/windows/pi/pi-common.ps1` helpers over plain `ssh`.
  Plain `ssh user@host` can hang or fail because it skips the configured Codex key, known_hosts file, `BatchMode=yes`, and timeout options.
* For worker logs, prefer `scripts/windows/pi/get-pi-worker-journal.ps1`.
  It avoids fragile remote shell quoting and filters noisy EF Core SQL command logs by default.
* Avoid remote shell pipelines with complex `grep -E` patterns, especially patterns containing `|`.
  If a remote command needs quotes, pipes, regexes, SQL, or multiline logic, send it as base64-encoded Python/SQL/script content and decode it on the Pi.
* If SSH reports `kex_exchange_identification: read: Connection reset`, treat it as a transient transport failure first.
  Retry once with the Pi helper scripts before drawing conclusions about the application.
* For PostgreSQL diagnostics, prefer `codex_ro` plus base64-encoded SQL.
  Remember that PostgreSQL identifiers such as `"Players"` and `"LifecycleEvents"` are case-sensitive and need double quotes.

## Encoding

Preserve UTF-8 text, including Swedish characters such as å, ä, and ö.

If terminal output shows mojibake such as Ã¥, Ã¤, or Ã¶, assume it may be a shell display encoding issue. Verify file bytes as UTF-8 before replacing non-ASCII text.

Do not convert Swedish text to ASCII approximations.

## Required reading before frontend implementation

For the OSRS redesign, do not treat the existing frontend as the visual baseline. The existing frontend is legacy structure. It may be substantially rewritten as long as current behavior and real API data usage are preserved.

A successful redesign should look substantially closer to the generated reference screenshots than to the previous dashboard. A dark theme over the existing layout is not sufficient.

Before making frontend changes, read these files in order:

```text
AGENTS.md
docs/PRODUCT.md
docs/DESIGN.md
docs/FRONTEND_MVP_IMPLEMENTATION.md
docs/CODEX_TASKS.md
docs/swedes_clan_tracker_missing_features.md
docs/swedes_clan_tracker_scope_review.md
docs/FRONTEND_MVP_PAUSE_NOTES.md
docs/OSRS_TEXTURE_RESEARCH.md
```

Also inspect the reference images folder before implementing UI work.

If any of those files are missing, stop and report which ones are missing before continuing.

## Local frontend preview against Pi API

For local frontend visual checks, Codex may run the Vite dev server locally and proxy `/api` to the Raspberry Pi API.

Read:

```text
docs/LOCAL_DEV_PROXY.md
```

If present, Codex may also read this untracked local-only file:

```text
docs/LOCAL_DEV_PRIVATE.md
```

`docs/LOCAL_DEV_PRIVATE.md` may contain temporary dev credentials for browser smoke checks. It must never be committed.

For local frontend preview, use:

```powershell
cd swedesclantracker-frontend
$env:VITE_API_PROXY_TARGET="http://<api-host-or-ip>"
npm run dev -- --host 127.0.0.1 --port 5173
```

Production safety rules:

- Keep frontend API calls relative to `/api`.
- Do not hard-code the Pi IP into production client code.
- Do not modify `apiClient.js` for local preview.
- Do not run Raspberry Pi deploy scripts unless the user explicitly asks.
- Do not commit temporary usernames, passwords, tokens, cookies, or private connection details.

## Current implementation phase

The current phase is:

> Frontend data-first overhaul.

Primary goal:

> Make the app a polished OSRS-inspired clan operations console while preserving existing behavior and using only currently available API data.

Hard constraints for this phase:

* Do not change `SwedesClanTracker.Core`.
* Do not change `SwedesClanTracker.Worker`.
* Do not add migrations.
* Do not change sync behavior.
* Do not invent backend data.
* Do not add fake production metrics or placeholder mockup content.
* Do not introduce heavy UI frameworks.
* Do not use official/copyrighted OSRS UI assets.
* Keep Raspberry Pi deployment assumptions in mind.

Missing future data must be hidden, shown as unavailable, or represented as `null`/future-state.

## Frontend implementation rule

Do not invent backend data.

Render the OSRS UI using existing API data, and mark missing future data as unavailable/null/future.

Examples of data that must not be faked:

* total XP
* combat level
* account build
* per-skill breakdown
* boss KC
* raid KC
* weekly XP gained
* drops/splits
* collection-log item details
* advanced RSN confidence percentages
* name-change XP/KC matching metrics
* competitions/leagues

If a reference image shows one of these but the API does not provide it, render an unavailable state or omit the field.

## Frontend architecture rules

Frontend work belongs in:

```text
swedesclantracker-frontend
```

Stack:

* React 18.
* Vite.
* Tailwind CSS.
* No heavy UI framework unless explicitly requested.
* Prefer custom, lightweight components.
* Keep bundle size and runtime overhead modest for Raspberry Pi deployment.

Recommended structure:

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

Do not preserve the current frontend structure just because it exists.

The current `App.jsx` page/table layout should be treated as legacy reference, not as the target architecture.

Avoid making `App.jsx` larger and more monolithic.

## View-model rule

Pages should not bind directly to raw API DTOs.

Use frontend view-model mappers between API responses and page components.

Example:

```js
const dashboard = mapHomeToDashboardViewModel(home, status);
const roster = mapRosterToRosterViewModel(rosterResponse);
```

This keeps the UI stable while future API/Core/Worker support is added later.

## Feature availability rule

Unsupported future features must be explicit.

Use a feature availability registry or equivalent component props.

Example:

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
  }
};
```

Components should render clean unavailable states when optional future fields are missing.

## Repository context

This repository contains SwedesClanTracker, a local full-stack OSRS clan rank tracker.

Main projects:

* `SwedesClanTracker.Api`: ASP.NET Core Web API with cookie auth.
* `SwedesClanTracker.Worker`: continuous roster/stat sync worker and Discord bot.
* `SwedesClanTracker.Core`: EF Core models and rank/lifecycle logic.
* `swedesclantracker-frontend`: React + Vite + Tailwind frontend app.

Production target:

* Raspberry Pi first.
* PostgreSQL.
* systemd.
* nginx.
* LAN-only deployment.
* Keep the frontend lightweight and fast.

## Project boundary rules

Frontend redesign may make substantial changes to:

```text
swedesclantracker-frontend
```

API project changes are allowed only when they are additive and support frontend UX without changing Core/Worker behavior.

For this frontend-only MVP, prefer no API changes. If API changes are needed, prefer:

* read-only projection endpoints;
* frontend-friendly DTOs;
* wrappers around existing behavior;
* endpoint shapes that reduce frontend guesswork;
* no changes to existing endpoint contracts.

Good API additions may include:

```text
GET /api/app/home
GET /api/app/activity
GET /api/app/roster
GET /api/app/players/{id}/profile
GET /api/admin/work-queue
GET /api/admin/work-queue/preflight
```

Names may differ if a better convention already exists in the project.

API DTOs should be frontend-friendly and explicit.

Avoid leaking database/entity structure directly into frontend contracts when a more useful view model would improve UX.

Do not move business logic into the frontend when the API can provide a safer, clearer decision model.

## Core and Worker safety

Avoid changing `SwedesClanTracker.Core`.

Do not change `SwedesClanTracker.Worker` unless explicitly requested.

Core changes are only acceptable when the redesign cannot be reasonably supported through frontend work, API-layer queries, DTOs, or endpoints.

Before changing Core, ask the user for approval.

When proposing a Core change, include:

* why API/frontend alone is insufficient
* what Core files/classes would change
* what frontend/API feature depends on it
* what Worker behavior could be affected
* how to validate Worker safety
* what tests/builds should be run

Worker project must remain compatible.

If any shared Core model, enum, service, database behavior, or lifecycle logic changes, validate that:

* `SwedesClanTracker.Worker` still builds.
* worker lifecycle/rank/promotion behavior is not changed accidentally.
* Discord bot behavior is not changed accidentally.
* existing Pi deployment behavior is not changed accidentally.

At minimum, after any Core change, validate:

```bash
dotnet build
```

and specifically ensure:

```text
SwedesClanTracker.Api
SwedesClanTracker.Worker
SwedesClanTracker.Core
```

still build successfully.

## Preserve

Preserve:

* Cookie-based auth unless explicitly changing auth behavior.
* `/api` base path.
* Existing admin capabilities.
* Existing Raspberry Pi deployment assumptions.
* Existing backend behavior unless API changes are explicitly part of the redesign plan.
* Worker compatibility.
* Discord bot behavior.
* Current rank/lifecycle behavior.

## Product direction

This is not a public marketing website.

SwedesClanTracker should become a purpose-built OSRS clan tracker frontend, not just an admin table dashboard.

It should support:

* tracker health understanding
* clan/member overview
* roster exploration
* player profile entry points
* rank/stat progression over time in the future
* recent meaningful clan activity
* admin work queues
* promotion decisions
* missing/new player review
* rename/merge review
* operational readiness

The dashboard should remain the main overview and should be useful as a daily landing page.

Admin-heavy work should live in a dedicated admin/work queue area.

Do not split full admin/user modes yet unless explicitly requested, but design the frontend so a future non-admin/member-safe experience is possible.

## UX direction

The redesign should not be a visual reskin of the current app.

The current app structure is too page/table-shaped:

* simple dashboard
* simple activity feed
* simple players table
* simple promotions/review tables
* basic work queue wrapper

A successful redesign should reconsider the product shape.

Prioritize:

* useful main dashboard overview
* member/roster exploration
* future player profile and stat/rank history support
* clear admin work queue
* safer high-impact admin actions
* better activity/log signal
* fewer raw tables where a richer decision surface would be better

Tables are allowed when they are the right interaction, especially for dense roster scanning.

Tables should not be the default answer for every feature.

## Design direction

The redesigned frontend should feel like a polished OSRS clan operations console.

Use:

* dark stone-like panels;
* muted gold/brass borders;
* compact card/table layouts;
* beveled buttons;
* readable status pills;
* custom OSRS-inspired icons where helpful;
* accessible contrast and focus states.

Avoid:

* generic SaaS dashboard styling;
* futuristic/cyberpunk styling;
* decorative charts that do not help decisions;
* official OSRS logos or copied UI assets;
* overusing tables where decision cards are better;
* overly sparse layouts that hide useful information.

The UI should help an admin quickly answer:

* Is the tracker healthy?
* What changed recently?
* Is there work waiting?
* How is the clan/member roster looking?
* Who needs review?
* Which promotions are pending?
* Which actions are safe to take now?
* Where should I go next?

Dangerous/destructive actions must be visually distinct and should have confirmation where appropriate.

Busy/disabled states must be clear when actions are running.

Long usernames, missing values, stale timestamps, and large datasets must remain readable.

## Implementation order

Follow `/docs/CODEX_TASKS.md`.

Default order:

1. OSRS design-system foundation.
2. Dashboard MVP.
3. Members roster MVP.
4. Review queues MVP.
5. Player profile MVP.
6. Activity log polish.
7. Cleanup and consistency pass.

Do not expand scope inside a task.

## Product Design / Figma tooling

Product Design or Figma tooling may be used after the first frontend pass to validate and refine:

* spacing;
* hierarchy;
* component consistency;
* screen comparisons against reference images;
* design-system variants.

Do not use Product Design/Figma tooling to:

* generate production code without adapting it to this repo;
* invent backend fields;
* decide product scope;
* add official OSRS assets;
* bypass the frontend view-model layer.

## Validation commands

After frontend changes, run:

```bash
cd swedesclantracker-frontend
npm run build
npm run lint
```

If lint already fails before changes, document the pre-existing failure and avoid making it worse.

After API changes, run the relevant .NET build command from the repository root:

```bash
dotnet build
```

If the full solution build is slow or unavailable, explain what was run and what was not run.

## Final response expectations for Codex

When finishing a task, report:

* files changed;
* what was implemented;
* what was intentionally left unavailable/future;
* whether Core/Worker/API were untouched;
* validation commands run and results;
* any pre-existing failures or skipped checks.

## Things to avoid

Avoid:

* generic SaaS dashboard styling
* decorative charts that do not help decisions
* large dependencies
* overly sparse layouts that hide useful information
* hiding important admin actions too deeply
* preserving current frontend structure by default
* making `App.jsx` larger and more monolithic
* building another reskinned table dashboard
* changing Core without explicit approval
* changing Worker behavior without explicit approval
* breaking Worker compatibility
* adding fake data to make reference-image metrics appear real
* hard-coding future metrics that are not currently supported
