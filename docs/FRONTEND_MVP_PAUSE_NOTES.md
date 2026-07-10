# Frontend overhaul status

The previous mockup-led frontend sequence is retired. The app has been reset around current API data and the product surfaces that can be supported today.

## Current state

- The legacy mockup styling and placeholder dashboard content have been removed.
- Dashboard, roster, review queues, profiles, activity, and readiness use real API responses.
- Unsupported XP, combat, boss, raid, collection-log, confidence, and competition metrics are not rendered.
- Core, Worker, and API contracts remain untouched.

## Verification checklist

- Test with an authenticated local frontend proxy against the Pi API.
- Check loading, empty, stale, error, long-name, and mobile states.
- Run `npm run build` and `npm run lint`.
- Confirm `docs/CURRENT_WORKFLOWS_AND_EDGE_CASES.md` remains an untracked local workflow document.

## Future work

Only add richer statistics after their source, persistence, API contract, and ownership are explicitly defined. Keep the frontend honest while those capabilities are unavailable.
