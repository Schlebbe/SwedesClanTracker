# Frontend implementation guide

## Mission

Maintain a lightweight React/Vite/Tailwind frontend that presents the existing SwedesClanTracker API clearly and honestly. The current implementation is an OSRS-inspired clan operations console, not a mockup of unsupported game statistics.

## Architecture

Pages live in `swedesclantracker-frontend/src/surfaces`. API responses are mapped in `src/data/viewModels` before they reach components. Shared shell and OSRS components live under `src/components`.

The main app orchestrates authentication, navigation, loading, and API calls. It should not become a large page component or contain business rules.

## Current API-backed surfaces

| Surface | Source | Purpose |
| --- | --- | --- |
| Dashboard | `/api/app/home`, `/api/status` | health, roster summary, open work, meaningful changes |
| Clan members | `/api/app/roster` | search, status, freshness, flags, profile entry |
| Player profiles | `/api/app/players/{id}/profile` | persisted player state, cases, lifecycle events |
| Review queues | `/api/app/admin-queue`, `/api/app/admin-queue/{id}` | inspect current administrative work |
| Activity log | `/api/app/clan-log` | lifecycle and tracker activity |
| Readiness | `/api/app/readiness` | runtime and configuration signals |

## Non-goals

Do not add mock statistics, fake charts, placeholder quick actions, visual-target annotations, or developer-facing explanatory copy to production UI. Do not change Core, Worker, sync behavior, or existing API contracts as part of frontend work.

## Validation

From `swedesclantracker-frontend` run:

```text
npm run build
npm run lint
```

If API code changes later, also run the relevant .NET build from the repository root.
