# Swedes Clan Tracker frontend

The frontend is a React/Vite/Tailwind clan operations console. It uses the existing authenticated `/api` endpoints for tracker health, roster exploration, player profiles, review queues, activity, and readiness.

## Local development

```powershell
$env:VITE_API_PROXY_TARGET="http://<api-host-or-ip>"
npm run dev -- --host 127.0.0.1 --port 5173
```

Production client requests remain relative to `/api`; the proxy target is a local development setting only.

## Validation

```text
npm run build
npm run lint
```

The UI intentionally does not display statistics that the current API does not provide, including XP, combat, skill, boss, raid, and collection-log metrics.
