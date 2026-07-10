# SwedesClanTracker product direction

SwedesClanTracker is an authenticated, purpose-built OSRS clan operations console for the clan team. It is not a marketing site and it must not imply data that the tracker does not collect.

## Current product

The frontend is a data-first overhaul of the old dashboard. It gives an operator a clear path through:

- tracker health and current roster summary;
- clan member search, status, sync freshness, and review flags;
- player profile entry points using persisted player and lifecycle data;
- promotion, missing-player, rename, and merge review queues;
- recent clan lifecycle activity;
- runtime and configuration readiness.

The API remains the source of truth. Pages use frontend view-model mappers rather than binding directly to API DTOs.

## Data truth rules

Production UI may show only values returned by the API or values derived from those values. It must not fabricate XP, combat level, skill breakdowns, boss or raid KC, collection-log details, confidence percentages, weekly gains, drops, splits, competitions, or charts.

When a future feature is not supported by persisted data, it is omitted from the main workflow. A clear unavailable state is acceptable where the user needs to understand why a feature is absent.

## Boundaries

- Frontend work belongs in `swedesclantracker-frontend`.
- Existing API contracts and cookie authentication are preserved.
- Core and Worker behavior are out of scope for the UI overhaul.
- API additions, if required later, must be read-only projections and must not change sync behavior.
- The Raspberry Pi deployment remains the primary operational target.

## Product priorities

1. Make tracker health and pending work obvious on the dashboard.
2. Make the roster useful for scanning and investigation.
3. Keep review actions explicit and safe.
4. Keep activity and readiness grounded in operational data.
5. Add richer history only when the backend actually persists it.
