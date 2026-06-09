# CODEX_TEXTURE_PROMPT.md

Generated: `2026-06-09T14:27:51+00:00`

Use this after Prompt 7 cleanup, not before.

```text
Implement a CSS-only OSRS texture/depth pass.

Read:
- AGENTS.md
- docs/DESIGN.md
- docs/FRONTEND_MVP_IMPLEMENTATION.md
- docs/OSRS_TEXTURE_RESEARCH.md
- reference screenshots

Goal:
Make the current OSRS frontend feel less flat and closer to the reference images while preserving readability and frontend performance.

Scope:
- improve AppShell background depth;
- improve StonePanel stone/parchment feel;
- improve BeveledButton brass/edge treatment;
- improve sidebar/topbar depth;
- improve table header/panel header depth;
- keep table body rows readable.

Allowed:
- CSS gradients;
- inset shadows;
- pseudo-elements;
- border-image / layered gradient borders;
- tiny inline SVG data-URI noise/patterns;
- CSS variables.

Not allowed:
- external dependencies;
- downloaded texture assets;
- official OSRS assets;
- fake data;
- API/Core/Worker/database/migration changes;
- changing sync logic;
- making dense table text harder to read.

Keep changes low-risk and scoped mostly to:
- osrs-theme.css
- shared OSRS/shell components only if class hooks are needed

Run:
cd swedesclantracker-frontend
npm run build
npm run lint

Report:
- files changed
- texture/depth changes made
- any readability tradeoffs
- whether backend/Core/Worker/API were untouched
- validation results
```
