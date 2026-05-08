import { useCallback, useEffect, useMemo, useState } from "react";
import moment from "moment";
import "moment/locale/sv";

const apiBase = "/api";
const pages = ["Dashboard", "Activity", "Players", "Promotions", "Review", "Settings"];
const dateTimeFormat = "D MMM YYYY HH:mm";
const activityFilters = [
  { key: "all", label: "Everything" },
  { key: "players", label: "Players" },
  { key: "promotions", label: "Promotions" },
  { key: "discord", label: "Discord" },
  { key: "commands", label: "Commands" },
  { key: "review", label: "Review" },
  { key: "system", label: "System" },
];
moment.locale("sv");

async function call(path, options = {}) {
  const res = await fetch(`${apiBase}${path}`, {
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    ...options,
  });
  const text = await res.text();
  if (!res.ok) {
    const details = text?.trim();
    throw new Error(details ? `Request failed ${res.status}: ${details}` : `Request failed ${res.status}`);
  }
  if (res.status === 204) return null;
  if (!text) return null;
  return JSON.parse(text);
}

const parseDate = (v) => {
  if (!v || typeof v !== "string") return null;
  const parsed = moment(v, moment.ISO_8601, true);
  return parsed.isValid() ? parsed : null;
};
const fmt = (v) => {
  const parsed = parseDate(v);
  return parsed ? parsed.local().format(dateTimeFormat) : "-";
};
const rel = (v) => {
  const parsed = parseDate(v);
  return parsed ? parsed.local().fromNow() : "aldrig";
};
const displayDetailLabel = (label) => label.replace(/\s*\bUTC\b\s*/gi, " ").replace(/\s+/g, " ").trim();
const fmtDetail = (label, value) => {
  const parsed = parseDate(value);
  if (!parsed) return value;
  return fmt(value);
};
const cmp = (a, b, dir = "asc") => {
  if (a == null && b == null) return 0;
  if (a == null) return dir === "asc" ? -1 : 1;
  if (b == null) return dir === "asc" ? 1 : -1;
  if (a < b) return dir === "asc" ? -1 : 1;
  if (a > b) return dir === "asc" ? 1 : -1;
  return 0;
};

export default function App() {
  const [page, setPage] = useState("Dashboard");
  const [loggedIn, setLoggedIn] = useState(false);
  const [data, setData] = useState(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [liveStatus, setLiveStatus] = useState(null);
  const [login, setLogin] = useState({ username: "admin", password: "changeme" });

  const load = useCallback(async () => {
    try {
      setError("");
      if (page === "Dashboard") setData(await call("/dashboard"));
      if (page === "Activity") setData(await call("/activity"));
      if (page === "Players") setData(await call("/players"));
      if (page === "Promotions") setData(await call("/promotions"));
      if (page === "Review") setData(await call("/review/queue"));
      if (page === "Settings") setData(await call("/settings"));
      setLoggedIn(true);
    } catch {
      setLoggedIn(false);
      setData(null);
    }
  }, [page]);

  useEffect(() => { load(); }, [load]);

  const loadStatus = useCallback(async () => {
    if (!loggedIn) return;
    try {
      setLiveStatus(await call("/status"));
    } catch {
      setLiveStatus(null);
    }
  }, [loggedIn]);

  useEffect(() => {
    loadStatus();
    if (!loggedIn) return undefined;
    const timer = window.setInterval(loadStatus, 2000);
    return () => window.clearInterval(timer);
  }, [loggedIn, loadStatus]);

  async function doLogin(e) {
    e.preventDefault();
    try {
      setError("");
      const res = await fetch(`${apiBase}/auth/login`, {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(login),
      });
      if (!res.ok) {
        setError(res.status === 401 ? "Invalid username or password" : `Login failed (${res.status})`);
        return;
      }
      await load();
    } catch {
      setError("Login failed");
    }
  }

  async function action(fn) {
    setBusy(true);
    setError("");
    try {
      await fn();
      await load();
    } catch (e) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  }

  if (!loggedIn) {
    return (
      <main className="min-h-screen p-4 sm:p-8 flex items-center justify-center">
        <form className="w-full max-w-md rounded-2xl bg-white shadow-xl border border-slate-200 p-6 sm:p-8" onSubmit={doLogin}>
          <h1 className="text-2xl font-semibold mb-6">Clan Tracker Login</h1>
          <input className="w-full rounded-lg border border-slate-300 px-3 py-2 mb-3" value={login.username} onChange={(e) => setLogin({ ...login, username: e.target.value })} />
          <input className="w-full rounded-lg border border-slate-300 px-3 py-2 mb-4" type="password" value={login.password} onChange={(e) => setLogin({ ...login, password: e.target.value })} />
          <button className="w-full rounded-lg bg-blue-700 text-white py-2 font-medium">Login</button>
          {error ? <p className="text-rose-700 mt-3">{error}</p> : null}
        </form>
      </main>
    );
  }

  return (
    <main className="min-h-screen p-4 sm:p-6 lg:p-8">
      <div className="max-w-7xl mx-auto">
        <div className="flex flex-wrap gap-2 mb-4">
          {pages.map((p) => (
            <button key={p} onClick={() => setPage(p)} className={`px-4 py-2 rounded-lg text-sm font-medium border ${page === p ? "bg-blue-700 text-white border-blue-700" : "bg-white text-slate-700 border-slate-300 hover:bg-slate-50"}`}>{p}</button>
          ))}
          <button onClick={load} className="px-4 py-2 rounded-lg text-sm font-medium bg-emerald-600 text-white hover:bg-emerald-700">Refresh</button>
        </div>
        <section className="rounded-2xl bg-white shadow-lg border border-slate-200 overflow-hidden">
          <div className="px-5 py-4 border-b border-slate-200 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
            <h1 className="text-xl font-semibold">{page}</h1>
            <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
              {error ? <span className="text-sm text-rose-700">{error}</span> : null}
              <LiveStatus status={liveStatus} />
            </div>
          </div>
          <div className="p-4 sm:p-5 overflow-x-auto">
            {page === "Dashboard" && data ? <Dashboard data={data} /> : null}
            {page === "Activity" && Array.isArray(data) ? <ActivityTimeline rows={data} /> : null}
            {page === "Players" && Array.isArray(data) ? <PlayersTable rows={data} /> : null}
            {page === "Promotions" && Array.isArray(data) ? <PromotionsTable rows={data} action={action} busy={busy} /> : null}
            {page === "Review" && Array.isArray(data) ? <ReviewTable rows={data} action={action} busy={busy} /> : null}
            {page === "Settings" && data ? <SettingsTable data={data} /> : null}
          </div>
        </section>
      </div>
    </main>
  );
}

function FilterInput({ value, onChange, placeholder = "Filter..." }) {
  return <input value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} className="w-full border border-slate-300 rounded px-2 py-1 text-xs" />;
}
function Table({ children }) { return <table className="min-w-full text-sm">{children}</table>; }
function SortHeader({ label, field, sort, setSort }) {
  const active = sort.field === field;
  const direction = active ? sort.dir : null;
  return (
    <button
      className="font-semibold hover:text-blue-700"
      onClick={() => setSort(active ? { field, dir: direction === "asc" ? "desc" : "asc" } : { field, dir: "asc" })}
    >
      {label} {active ? (direction === "asc" ? "↑" : "↓") : ""}
    </button>
  );
}

function LiveStatus({ status }) {
  const components = (status?.components ?? []).filter((item) => item.component !== "API");
  if (!components.length) {
    return (
      <div className="rounded-md border border-slate-200 bg-slate-50 px-3 py-2 text-xs text-slate-500 sm:w-[36rem]">
        Waiting for worker heartbeat...
      </div>
    );
  }

  const isWorking = (item) => item.state === "Working" || item.state === "Processing player" || item.state === "Syncing roster";
  const latestSync = components.find((item) => item.component === "Latest Sync");
  const recentEvent = components.find((item) => item.component === "Recent Event");
  const workerComponents = components.filter((item) => item.component !== "Latest Sync" && item.component !== "Recent Event");
  const currentWorker = workerComponents.find((item) => item.currentPlayer) ?? workerComponents.find(isWorking) ?? workerComponents.find((item) => item.component === "Tracker");
  const toneSource = currentWorker ?? latestSync ?? recentEvent ?? components[0];
  const tone = toneSource.isOffline ? "bg-rose-500" : toneSource.isStale ? "bg-amber-500" : toneSource.state === "Error" ? "bg-rose-500" : isWorking(toneSource) ? "bg-blue-500" : "bg-emerald-500";
  const syncAge = latestSync?.heartbeatAt ? rel(latestSync.heartbeatAt) : "";
  const eventAge = recentEvent?.heartbeatAt ? rel(recentEvent.heartbeatAt) : "";
  const nowText = currentWorker?.currentPlayer
    ? `Now syncing ${currentWorker.currentPlayer}`
    : currentWorker
      ? currentWorker.message
      : null;

  return (
    <div className="w-full rounded-md border border-slate-200 bg-slate-50 px-3 py-2 sm:w-[42rem]">
      <div className="flex items-center gap-2">
        <span className={`h-2.5 w-2.5 shrink-0 rounded-full ${tone}`} />
        <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Live status</span>
        {nowText ? <span className="text-xs text-slate-500">{nowText}</span> : null}
      </div>
      <div className="mt-2 grid gap-2 md:grid-cols-2">
        <div className="rounded border border-slate-200 bg-white px-2 py-1.5">
          <div className="text-xs font-semibold text-slate-500">Latest synced player</div>
          <div className="text-sm font-semibold leading-snug text-slate-900">{latestSync?.currentPlayer ?? "No player synced yet"}</div>
          {latestSync ? <div className="text-xs text-slate-500">{fmt(latestSync.heartbeatAt)} · {syncAge}</div> : null}
        </div>
        {recentEvent ? (
          <div className="rounded border border-slate-200 bg-white px-2 py-1.5">
            <div className="text-xs font-semibold text-slate-500">Recent event</div>
            <div className="text-sm font-semibold leading-snug text-slate-900">{recentEvent.currentPlayer ? `${recentEvent.currentPlayer}: ` : ""}{recentEvent.state}</div>
            <div className="text-xs text-slate-500">{fmt(recentEvent.heartbeatAt)} · {eventAge}</div>
          </div>
        ) : null}
      </div>
      {workerComponents.length ? (
        <div className="mt-2 flex flex-wrap gap-1">
          {workerComponents.map((item) => (
            <span key={item.component} className="rounded bg-white px-2 py-0.5 text-xs text-slate-500">{item.component}: {item.state}</span>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function ActivityTimeline({ rows }) {
  const [filter, setFilter] = useState("all");
  const [query, setQuery] = useState("");
  const counts = useMemo(() => {
    const next = Object.fromEntries(activityFilters.map((x) => [x.key, 0]));
    next.all = rows.length;
    for (const row of rows) {
      for (const group of row.groups ?? []) {
        if (next[group] != null) next[group] += 1;
      }
    }
    return next;
  }, [rows]);
  const filtered = useMemo(() => {
    const needle = query.trim().toLowerCase();
    return rows.filter((row) => {
      const inGroup = filter === "all" || row.groups?.includes(filter);
      if (!inGroup) return false;
      if (!needle) return true;
      const detailText = (row.details ?? []).map((x) => `${x.label} ${x.value}`).join(" ");
      return [
        row.title,
        row.description,
        row.eventType,
        row.status,
        row.player,
        row.actor,
        row.categoryLabel,
        detailText,
      ].filter(Boolean).join(" ").toLowerCase().includes(needle);
    });
  }, [rows, filter, query]);

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex flex-wrap gap-1 rounded-md border border-slate-200 bg-slate-50 p-1">
          {activityFilters.map((item) => (
            <button
              key={item.key}
              onClick={() => setFilter(item.key)}
              className={`rounded px-3 py-1.5 text-sm font-medium ${filter === item.key ? "bg-blue-700 text-white" : "text-slate-700 hover:bg-white"}`}
            >
              {item.label} <span className={filter === item.key ? "text-blue-100" : "text-slate-500"}>{counts[item.key] ?? 0}</span>
            </button>
          ))}
        </div>
        <div className="w-full lg:w-72">
          <FilterInput value={query} onChange={setQuery} placeholder="Search activity" />
        </div>
      </div>
      <div className="overflow-hidden rounded-md border border-slate-200">
        {filtered.length === 0 ? (
          <div className="p-4 text-sm text-slate-500">No activity found.</div>
        ) : filtered.map((row) => (
          <div key={row.id} className="grid gap-2 border-b border-slate-200 p-3 last:border-b-0 md:grid-cols-[170px_1fr]">
            <div className="text-xs text-slate-500">
              <div>{fmt(row.createdAt)}</div>
              <div>{rel(row.createdAt)}</div>
            </div>
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-2">
                <span className="rounded bg-slate-100 px-2 py-0.5 text-xs font-semibold text-slate-700">{row.categoryLabel}</span>
                <span className={row.status === "OPEN" ? "rounded bg-amber-100 px-2 py-0.5 text-xs font-semibold text-amber-800" : "rounded bg-emerald-100 px-2 py-0.5 text-xs font-semibold text-emerald-800"}>{row.status}</span>
                {row.actor ? <span className="rounded bg-indigo-50 px-2 py-0.5 text-xs font-semibold text-indigo-700">By {row.actor}</span> : null}
                <span className="text-xs text-slate-400">{row.eventType}</span>
              </div>
              <div className="mt-1 font-semibold text-slate-900">{row.title}</div>
              <div className="mt-0.5 text-sm text-slate-600">{row.description}</div>
              {row.details?.length ? (
                <div className="mt-2 flex flex-wrap gap-1.5">
                  {row.details.slice(0, 8).map((detail) => (
                    <span key={`${row.id}-${detail.label}`} className="rounded border border-slate-200 bg-white px-2 py-0.5 text-xs text-slate-600">
                      <span className="font-medium text-slate-700">{displayDetailLabel(detail.label)}:</span> {fmtDetail(detail.label, detail.value)}
                    </span>
                  ))}
                </div>
              ) : null}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function Dashboard({ data }) {
  return <Table><tbody><tr className="border-b"><th className="text-left py-2 pr-4 font-semibold">Total Players</th><td>{data.players}</td></tr><tr className="border-b"><th className="text-left py-2 pr-4 font-semibold">Pending Promotions</th><td>{data.pendingPromotions}</td></tr><tr className="border-b"><th className="text-left py-2 pr-4 font-semibold">Missing Players</th><td>{data.missing}</td></tr><tr><th className="text-left py-2 pr-4 font-semibold">Pending Review</th><td>{data.pendingReview}</td></tr></tbody></Table>;
}

function PlayersTable({ rows }) {
  const [sort, setSort] = useState({ field: "username", dir: "asc" });
  const sorted = useMemo(() => [...rows].sort((a, b) => {
    switch (sort.field) {
      case "username": return cmp(a.username, b.username, sort.dir);
      case "currentRank": return cmp(a.currentRank, b.currentRank, sort.dir);
      case "status": return cmp(a.status, b.status, sort.dir);
      case "totalLevel": return cmp(a.totalLevel, b.totalLevel, sort.dir);
      case "ehb": return cmp(a.ehb, b.ehb, sort.dir);
      case "ehp": return cmp(a.ehp, b.ehp, sort.dir);
      case "pets": return cmp((a.manualPetOverride ?? a.storedPetCount), (b.manualPetOverride ?? b.storedPetCount), sort.dir);
      case "lastSynced": return cmp(a.lastSynced ? new Date(a.lastSynced).getTime() : null, b.lastSynced ? new Date(b.lastSynced).getTime() : null, sort.dir);
      default: return 0;
    }
  }), [rows, sort]);

  return (
    <Table>
      <thead>
        <tr className="text-left border-b bg-slate-50">
          <th className="p-2"><SortHeader label="Username" field="username" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="Current" field="currentRank" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="Status" field="status" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="Total Level" field="totalLevel" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="EHB" field="ehb" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="EHP" field="ehp" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="Pets" field="pets" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="Last Synced" field="lastSynced" sort={sort} setSort={setSort} /></th>
        </tr>
      </thead>
      <tbody>
        {sorted.map((r) => {
          const pets = r.manualPetOverride ?? r.storedPetCount;
          return (
            <tr key={r.id} className="border-b">
              <td className="p-2 font-medium">{r.username}</td>
              <td className="p-2">{r.currentRank}</td>
              <td className="p-2">{r.status}</td>
              <td className="p-2">{r.totalLevel ?? "N/A"}</td>
              <td className="p-2">{r.ehb != null ? Number(r.ehb).toFixed(1) : "N/A"}</td>
              <td className="p-2">{r.ehp != null ? Number(r.ehp).toFixed(1) : "N/A"}</td>
              <td className="p-2">{pets > 0 ? pets : "N/A"}</td>
              <td className="p-2"><div>{fmt(r.lastSynced)}</div><div className="text-xs text-slate-500">{rel(r.lastSynced)}</div></td>
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}

function PromotionsTable({ rows, action, busy }) {
  const [sort, setSort] = useState({ field: "createdAt", dir: "desc" });
  const candidateTypeLabel = (candidateType) => {
    switch (candidateType) {
      case "wom_already_at_new_rank": return "Already correct in WOM";
      case "needs_wom_rank_update": return "Needs WOM rank update";
      default: return "WOM role unknown";
    }
  };
  const sorted = useMemo(() => [...rows].sort((a, b) => {
    switch (sort.field) {
      case "username": return cmp(a.username, b.username, sort.dir);
      case "oldRank": return cmp(a.oldRank, b.oldRank, sort.dir);
      case "newRank": return cmp(a.newRank, b.newRank, sort.dir);
      case "candidateType": return cmp(candidateTypeLabel(a.candidateType), candidateTypeLabel(b.candidateType), sort.dir);
      case "reason": return cmp(a.reason, b.reason, sort.dir);
      case "createdAt": return cmp(new Date(a.createdAt).getTime(), new Date(b.createdAt).getTime(), sort.dir);
      default: return 0;
    }
  }), [rows, sort]);
  async function approveAll() {
    if (!window.confirm(`Approve all ${rows.length} pending promotions?`)) return;
    await action(() => call("/promotions/approve-all", { method: "POST" }));
  }
  return (
    <>
      <div className="mb-3"><button disabled={busy || rows.length === 0} onClick={approveAll} className="px-4 py-2 rounded-lg bg-blue-700 text-white disabled:bg-slate-400">Approve All Pending</button></div>
      <Table>
        <thead>
          <tr className="text-left border-b bg-slate-50">
            <th className="p-2"><SortHeader label="Username" field="username" sort={sort} setSort={setSort} /></th>
            <th className="p-2"><SortHeader label="From" field="oldRank" sort={sort} setSort={setSort} /></th>
            <th className="p-2"><SortHeader label="To" field="newRank" sort={sort} setSort={setSort} /></th>
            <th className="p-2"><SortHeader label="Type" field="candidateType" sort={sort} setSort={setSort} /></th>
            <th className="p-2"><SortHeader label="Reason" field="reason" sort={sort} setSort={setSort} /></th>
            <th className="p-2"><SortHeader label="Created" field="createdAt" sort={sort} setSort={setSort} /></th>
            <th className="p-2">Actions</th>
          </tr>
        </thead>
        <tbody>
          {sorted.map((r) => <tr key={r.id} className="border-b align-top"><td className="p-2 font-medium">{r.username}</td><td className="p-2">{r.oldRank}</td><td className="p-2">{r.newRank}</td><td className="p-2">{candidateTypeLabel(r.candidateType)}</td><td className="p-2 max-w-xl">{r.reason}</td><td className="p-2">{fmt(r.createdAt)}</td><td className="p-2"><div className="flex gap-2"><button disabled={busy} className="px-3 py-1.5 rounded-md bg-emerald-600 text-white disabled:bg-slate-400" onClick={() => action(() => call(`/promotions/${r.id}/approve`, { method: "POST" }))}>Approve</button><button disabled={busy} className="px-3 py-1.5 rounded-md bg-rose-700 text-white disabled:bg-slate-400" onClick={() => action(() => call(`/promotions/${r.id}/dismiss`, { method: "POST" }))}>Dismiss</button></div></td></tr>)}
        </tbody>
      </Table>
    </>
  );
}

function ReviewTable({ rows, action, busy }) {
  const [sort, setSort] = useState({ field: "lastSeen", dir: "desc" });
  const sorted = useMemo(() => [...rows].sort((a, b) => {
    switch (sort.field) {
      case "username": return cmp(a.username, b.username, sort.dir);
      case "status": return cmp(a.status, b.status, sort.dir);
      case "currentRank": return cmp(a.currentRank, b.currentRank, sort.dir);
      case "eligibleRank": return cmp(a.eligibleRank, b.eligibleRank, sort.dir);
      case "lastSeen": return cmp(new Date(a.lastSeen).getTime(), new Date(b.lastSeen).getTime(), sort.dir);
      default: return 0;
    }
  }), [rows, sort]);
  return (
    <Table>
      <thead>
        <tr className="text-left border-b bg-slate-50">
          <th className="p-2"><SortHeader label="Username" field="username" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="Status" field="status" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="Current" field="currentRank" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="Eligible" field="eligibleRank" sort={sort} setSort={setSort} /></th>
          <th className="p-2"><SortHeader label="Last Seen" field="lastSeen" sort={sort} setSort={setSort} /></th>
          <th className="p-2">Review Actions</th>
        </tr>
      </thead>
      <tbody>
        {sorted.map((r) => <tr key={r.id} className="border-b"><td className="p-2 font-medium">{r.username}</td><td className="p-2">{r.status}</td><td className="p-2">{r.currentRank}</td><td className="p-2">{r.eligibleRank}</td><td className="p-2">{fmt(r.lastSeen)}</td><td className="p-2">{(r.status === "MISSING_PENDING_REVIEW" || r.status === "NEW_PENDING_REVIEW") ? <div className="flex gap-2"><button disabled={busy} className="px-3 py-1.5 rounded-md bg-emerald-600 text-white disabled:bg-slate-400" onClick={() => action(() => call(`/review/players/${r.id}/temple-missing/add`, { method: "POST" }))}>Add to Temple</button><button disabled={busy} className="px-3 py-1.5 rounded-md bg-rose-700 text-white disabled:bg-slate-400" onClick={() => action(() => call(`/review/players/${r.id}/temple-missing/remove-db`, { method: "POST" }))}>Remove from DB</button></div> : "-"}</td></tr>)}
      </tbody>
    </Table>
  );
}

function SettingsTable({ data }) {
  return <Table><tbody><tr className="border-b"><th className="text-left py-2 pr-4 font-semibold">API Rate Limit (per minute)</th><td>{data.apiRateLimitPerMinute}</td></tr><tr><th className="text-left py-2 pr-4 font-semibold">Connection String Configured</th><td>{String(data.connectionStringConfigured)}</td></tr></tbody></Table>;
}
