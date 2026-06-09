# Local Frontend Against a Remote API

For local frontend visual checks, run the Vite dev server locally and proxy `/api` to a remote API, such as a Raspberry Pi-hosted API.

The frontend must keep using relative `/api` calls. Do not replace them with absolute URLs.

## Command

PowerShell:

```powershell
cd swedesclantracker-frontend
$env:VITE_API_PROXY_TARGET="http://<api-host-or-ip>"
npm run dev -- --host 127.0.0.1 --port 5173
```

Open:

```text
http://127.0.0.1:5173
```

## Example proxy targets

Use whichever target matches your environment:

```text
http://127.0.0.1:5166
http://<raspberry-pi-ip>
http://<raspberry-pi-ip>:5166
```

Do not commit private LAN IPs, temporary credentials, tokens, cookies, or private connection details.

## Private local notes

If a specific developer needs private local details, keep them in this untracked file:

```text
docs/LOCAL_DEV_PRIVATE.md
```

That file may contain:

- actual LAN API target
- temporary local/dev username
- temporary local/dev password
- local-only smoke-test notes

`docs/LOCAL_DEV_PRIVATE.md` must never be committed.

## Production safety

`VITE_API_PROXY_TARGET` is used only by the Vite dev server proxy config.

Production builds still use same-origin `/api` behind nginx or the production web server.

Do not:

- change `apiClient.js` to use absolute URLs
- hard-code private IP addresses into production client code
- change backend/Core/Worker/database/deployment files for local frontend preview
- run Raspberry Pi deploy scripts unless explicitly requested by the user
- commit temporary usernames, passwords, tokens, cookies, private IPs, or private connection details

## Validation

After frontend changes, run:

```powershell
cd swedesclantracker-frontend
npm run build
npm run lint
```

The production build output must not contain private API targets or dev proxy values such as:

```text
VITE_API_PROXY_TARGET
<raspberry-pi-ip>
127.0.0.1:5166
```

A production build should continue to call same-origin `/api`.