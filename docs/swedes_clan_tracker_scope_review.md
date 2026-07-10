# Frontend scope review

## In scope now

- A substantial replacement of the legacy dashboard layout.
- Shared dark stone/brass styling, accessible controls, and responsive layouts.
- Dashboard, roster, profile entry points, review queues, activity, and readiness.
- View-model mappers that keep page components independent of raw API DTOs.
- Honest loading, empty, stale, error, and unavailable states.

## Out of scope now

- Changing `SwedesClanTracker.Core` or `SwedesClanTracker.Worker`.
- Changing sync, rank, lifecycle, Discord, authentication, or deployment behavior.
- Persisting new OSRS statistics or adding migrations.
- Fabricating game metrics to match reference imagery.
- Adding a large UI framework or official OSRS assets.

## Escalation rule

If a useful UI workflow requires data absent from the current API, first describe an additive read-only API projection. Core or Worker changes require explicit user approval with the affected files, behavior, validation, and deployment impact listed before implementation.
