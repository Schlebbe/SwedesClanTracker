import { useCallback, useEffect, useMemo, useState } from "react";

const apiBase = "/api";
const pages = ["Dashboard", "Activity", "Players", "Promotions", "Review", "Settings"];
const pageMeta = {
  Dashboard: { title: "Operations Dashboard", description: "Tracker health, urgent actions, and queue pressure." },
  Activity: { title: "Activity Feed", description: "Recent system and clan operations with filters." },
  Players: { title: "Member State", description: "Sortable member records, ranks, and sync freshness." },
  Promotions: { title: "Promotion Queue", description: "Pending candidates and action approval surface." },
  Review: { title: "Review Queue", description: "Ambiguous or risky states requiring operator decisions." },
  Settings: { title: "Runtime Settings", description: "Operational configuration and environment status." },
};

const activityFilters = [
  { key: "all", label: "Everything" },
  { key: "players", label: "Players" },
  { key: "promotions", label: "Promotions" },
  { key: "discord", label: "Discord" },
  { key: "commands", label: "Commands" },
  { key: "review", label: "Review" },
  { key: "system", label: "System" },
];

const dateTimeFormatter = new Intl.DateTimeFormat("sv-SE", {
  day: "numeric",
  month: "short",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
});

const relativeFormatter = new Intl.RelativeTimeFormat("en", { numeric: "auto" });

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
  if (!v) return null;
  const parsed = new Date(v);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
};

const fmt = (v) => {
  const parsed = parseDate(v);
  return parsed ? dateTimeFormatter.format(parsed) : "-";
};

const rel = (v) => {
  const parsed = parseDate(v);
  if (!parsed) return "never";
  const now = Date.now();
  const diffMs = parsed.getTime() - now;
  const absMs = Math.abs(diffMs);

  const units = [
    { unit: "year", ms: 1000 * 60 * 60 * 24 * 365 },
    { unit: "month", ms: 1000 * 60 * 60 * 24 * 30 },
    { unit: "week", ms: 1000 * 60 * 60 * 24 * 7 },
    { unit: "day", ms: 1000 * 60 * 60 * 24 },
    { unit: "hour", ms: 1000 * 60 * 60 },
    { unit: "minute", ms: 1000 * 60 },
    { unit: "second", ms: 1000 },
  ];

  for (const { unit, ms } of units) {
    if (absMs >= ms || unit === "second") {
      const value = Math.round(diffMs / ms);
      return relativeFormatter.format(value, unit);
    }
  }

  return "just now";
};

const displayDetailLabel = (label) => label.replace(/\s*\bUTC\b\s*/gi, " ").replace(/\s+/g, " ").trim();

const fmtDetail = (_, value) => {
  const parsed = parseDate(value);
  if (!parsed) return value;
  return fmt(value);
};

const isOlderThanHours = (value, hours) => {
  const parsed = parseDate(value);
  if (!parsed) return true;
  return parsed.getTime() < Date.now() - hours * 60 * 60 * 1000;
};

const cmp = (a, b, dir = "asc") => {
  if (a == null && b == null) return 0;
  if (a == null) return dir === "asc" ? -1 : 1;
  if (b == null) return dir === "asc" ? 1 : -1;
  if (a < b) return dir === "asc" ? -1 : 1;
  if (a > b) return dir === "asc" ? 1 : -1;
  return 0;
};

const isWorking = (item) => item?.state === "Working" || item?.state === "Processing player" || item?.state === "Syncing roster";

function toneFromComponent(item) {
  if (!item) return "neutral";
  if (item.isOffline || item.state === "Error") return "danger";
  if (item.isStale) return "warning";
  if (isWorking(item)) return "sync";
  return "success";
}

function summarizeLiveStatus(status) {
  const components = (status?.components ?? []).filter((item) => item.component !== "API");
  if (!components.length) {
    return {
      hasData: false,
      tone: "warning",
      title: "Waiting for worker heartbeat",
      subtitle: "No worker status has been reported yet.",
      latestSync: null,
      recentEvent: null,
      workerComponents: [],
      currentWorker: null,
    };
  }

  const latestSync = components.find((item) => item.component === "Latest Sync") ?? null;
  const recentEvent = components.find((item) => item.component === "Recent Event") ?? null;
  const workerComponents = components.filter((item) => item.component !== "Latest Sync" && item.component !== "Recent Event");
  const currentWorker = workerComponents.find((item) => item.currentPlayer) ?? workerComponents.find(isWorking) ?? workerComponents.find((item) => item.component === "Tracker") ?? null;
  const toneSource = currentWorker ?? latestSync ?? recentEvent ?? components[0];
  const tone = toneFromComponent(toneSource);

  const title = currentWorker?.currentPlayer
    ? `Syncing ${currentWorker.currentPlayer}`
    : currentWorker?.message
      ? currentWorker.message
      : tone === "success"
        ? "Tracker healthy"
        : tone === "warning"
          ? "Tracker warning"
          : tone === "danger"
            ? "Tracker issue"
            : "Tracker active";

  const subtitle = latestSync?.heartbeatAt
    ? `Last sync ${fmt(latestSync.heartbeatAt)} (${rel(latestSync.heartbeatAt)})`
    : "No player has been synced yet.";

  return {
    hasData: true,
    tone,
    title,
    subtitle,
    latestSync,
    recentEvent,
    workerComponents,
    currentWorker,
  };
}

const toneClasses = {
  neutral: "bg-[var(--surface-panel-raised)] text-[var(--text-secondary)] border-[var(--border-subtle)]",
  success: "bg-[var(--status-success-bg)] text-[var(--status-success-text)] border-[var(--status-success-border)]",
  warning: "bg-[var(--status-warning-bg)] text-[var(--status-warning-text)] border-[var(--status-warning-border)]",
  danger: "bg-[var(--status-danger-bg)] text-[var(--status-danger-text)] border-[var(--status-danger-border)]",
  sync: "bg-[var(--status-sync-bg-soft)] text-[var(--status-sync-text-soft)] border-[var(--status-sync-border)]",
};

const chipDotClasses = {
  neutral: "bg-[var(--text-muted)]",
  success: "bg-[var(--status-success)]",
  warning: "bg-[var(--status-warning)]",
  danger: "bg-[var(--status-danger)]",
  sync: "bg-[var(--status-info)]",
};

function ToneChip({ tone, children }) {
  const style = toneClasses[tone] ?? toneClasses.neutral;
  return <span className={`inline-flex items-center gap-1 rounded-md border px-2 py-0.5 text-xs font-semibold ${style}`}>{children}</span>;
}

function StatCard({ label, value, tone = "neutral", hint, className = "" }) {
  return (
    <article className={`rounded-xl border border-[var(--border-subtle)] bg-[var(--surface-panel)] p-4 sm:p-5 ${className}`}>
      <div className="text-xs uppercase tracking-wide text-[var(--text-muted)]">{label}</div>
      <div className="mt-2 text-2xl font-semibold tabular-nums text-[var(--text-primary)]">{value}</div>
      {hint ? <div className="mt-1 text-xs text-[var(--text-secondary)]">{hint}</div> : null}
      {tone !== "neutral" ? <div className={`mt-2 h-1 rounded-full ${chipDotClasses[tone]}`} /> : null}
    </article>
  );
}

function EmptyState({ title, message }) {
  return (
    <div className="rounded-xl border border-dashed border-[var(--border-subtle)] bg-[var(--surface-muted)] px-4 py-6 text-center">
      <p className="text-sm font-semibold text-[var(--text-primary)]">{title}</p>
      <p className="mt-1 text-sm text-[var(--text-secondary)]">{message}</p>
    </div>
  );
}

function LoadingBlock({ message = "Loading..." }) {
  return (
    <div className="rounded-xl border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-4 py-6 text-center text-sm text-[var(--text-secondary)]">
      {message}
    </div>
  );
}

function ErrorState({ title, message, onRetry }) {
  return (
    <div className="rounded-xl border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-4 py-5">
      <p className="text-sm font-semibold text-[var(--status-danger-text)]">{title}</p>
      <p className="mt-1 text-sm text-[var(--status-danger-text)]">{message}</p>
      {onRetry ? (
        <button
          onClick={onRetry}
          className="mt-3 min-h-11 rounded-md border border-[var(--status-danger-border)] px-3 py-2 text-sm font-semibold text-[var(--status-danger-text)] transition hover:bg-[var(--surface-panel-raised)]"
        >
          Retry
        </button>
      ) : null}
    </div>
  );
}

function SectionCard({ title, subtitle, right, children, className = "" }) {
  return (
    <section className={`rounded-xl border border-[var(--border-subtle)] bg-[var(--surface-panel)] p-4 sm:p-5 ${className}`}>
      <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-[var(--text-muted)]">{title}</h2>
          {subtitle ? <p className="mt-1 text-sm text-[var(--text-secondary)]">{subtitle}</p> : null}
        </div>
        {right}
      </div>
      {children}
    </section>
  );
}

export default function App() {
  const [page, setPage] = useState("Dashboard");
  const [loggedIn, setLoggedIn] = useState(false);
  const [data, setData] = useState(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(false);
  const [liveStatus, setLiveStatus] = useState(null);
  const [lastLoadedAt, setLastLoadedAt] = useState(null);
  const [login, setLogin] = useState({ username: "admin", password: "changeme" });

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setError("");
      if (page === "Dashboard") setData(await call("/dashboard"));
      if (page === "Activity") setData(await call("/activity"));
      if (page === "Players") setData(await call("/players"));
      if (page === "Promotions") setData(await call("/promotions"));
      if (page === "Review") setData(await call("/review/queue"));
      if (page === "Settings") setData(await call("/settings"));
      setLoggedIn(true);
      setLastLoadedAt(new Date().toISOString());
    } catch (e) {
      const message = e?.message ?? "Failed loading data.";
      if (message.includes("401") || message.includes("403")) {
        setLoggedIn(false);
      } else {
        setError(message);
      }
      setData(null);
    } finally {
      setLoading(false);
    }
  }, [page]);

  useEffect(() => {
    load();
  }, [load]);

  const loadStatus = useCallback(async () => {
    if (!loggedIn) return;
    try {
      setLiveStatus(await call("/status"));
    } catch {
      setLiveStatus(null);
    }
  }, [loggedIn]);

  const statusPollMs = page === "Dashboard" ? 2000 : 10000;

  useEffect(() => {
    if (!loggedIn) return undefined;
    loadStatus();

    const tick = () => {
      if (document.visibilityState === "visible") {
        loadStatus();
      }
    };

    const onVisibilityChange = () => {
      if (document.visibilityState === "visible") {
        loadStatus();
      }
    };

    const timer = window.setInterval(tick, statusPollMs);
    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, [loggedIn, loadStatus, statusPollMs]);

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
      setError(e?.message ?? "Action failed");
    } finally {
      setBusy(false);
    }
  }

  const statusSummary = useMemo(() => summarizeLiveStatus(liveStatus), [liveStatus]);

  if (!loggedIn) {
    return (
      <main className="min-h-screen bg-[var(--surface-canvas)] px-4 py-8 text-[var(--text-primary)] sm:px-8">
        <div className="mx-auto flex min-h-[80vh] max-w-md items-center">
          <form className="w-full rounded-2xl border border-[var(--border-subtle)] bg-[var(--surface-panel)] p-6 shadow-2xl sm:p-8" onSubmit={doLogin}>
            <p className="text-xs uppercase tracking-[0.2em] text-[var(--text-muted)]">SwedesClanTracker</p>
            <h1 className="mt-2 text-2xl font-semibold text-[var(--text-primary)]">Clan Operations Login</h1>
            <p className="mt-2 text-sm text-[var(--text-secondary)]">Authenticate to access tracker health, review queues, and promotion actions.</p>
            <label className="mt-6 block text-xs font-semibold uppercase tracking-wide text-[var(--text-muted)]">Username</label>
            <input
              className="mt-1 w-full rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2 text-sm text-[var(--text-primary)] outline-none ring-0 transition focus:border-[var(--status-sync-text)]"
              value={login.username}
              onChange={(e) => setLogin({ ...login, username: e.target.value })}
              autoComplete="username"
            />
            <label className="mt-4 block text-xs font-semibold uppercase tracking-wide text-[var(--text-muted)]">Password</label>
            <input
              className="mt-1 w-full rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2 text-sm text-[var(--text-primary)] outline-none ring-0 transition focus:border-[var(--status-sync-text)]"
              type="password"
              value={login.password}
              onChange={(e) => setLogin({ ...login, password: e.target.value })}
              autoComplete="current-password"
            />
            <button className="mt-5 min-h-11 w-full rounded-lg border border-[var(--primary-accent-border)] bg-[var(--primary-accent)] py-2 font-semibold text-[var(--surface-canvas)] transition hover:bg-[var(--primary-accent-hover)]">
              Login
            </button>
            {error ? <p className="mt-3 rounded-md border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-text)]">{error}</p> : null}
          </form>
        </div>
      </main>
    );
  }

  const pageInfo = pageMeta[page] ?? { title: page, description: "" };

  return (
    <main className="min-h-screen bg-[var(--surface-canvas)] px-4 py-5 text-[var(--text-primary)] sm:px-6 lg:px-8 lg:py-6">
      <div className="mx-auto max-w-[1400px] space-y-5">
        <header className="rounded-2xl border border-[var(--border-subtle)] bg-[var(--surface-panel)] p-4 sm:p-5">
          <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-start">
            <div>
              <p className="text-xs uppercase tracking-[0.2em] text-[var(--text-muted)]">SwedesClanTracker</p>
              <h1 className="mt-1 text-2xl font-semibold text-[var(--text-primary)]">{pageInfo.title}</h1>
              <p className="mt-1 text-sm text-[var(--text-secondary)]">{pageInfo.description}</p>
            </div>
            <div className="flex flex-col gap-2 lg:items-end">
              <div className="flex flex-wrap items-center gap-2">
                <button
                  onClick={load}
                  disabled={loading}
                  className="min-h-11 rounded-lg border border-[var(--primary-accent-border)] bg-[var(--primary-accent)] px-4 py-2 text-sm font-semibold text-[var(--surface-canvas)] transition hover:bg-[var(--primary-accent-hover)] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {loading ? "Refreshing..." : "Refresh"}
                </button>
                {busy ? <ToneChip tone="warning">Action in progress</ToneChip> : null}
                {lastLoadedAt ? <ToneChip tone="neutral">Loaded {rel(lastLoadedAt)}</ToneChip> : null}
              </div>
              {error ? <div className="rounded-md border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-text)]">{error}</div> : null}
            </div>
          </div>
          <div className="mt-4 border-t border-[var(--border-subtle)] pt-4">
            <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-6">
              {pages.map((p) => (
                <button
                  key={p}
                  onClick={() => setPage(p)}
                  className={`min-h-11 rounded-lg border px-3 py-2 text-sm font-medium text-left transition ${
                    page === p
                      ? "border-[var(--primary-accent-border)] bg-[var(--primary-accent)] text-[var(--surface-canvas)]"
                      : "border-[var(--border-subtle)] bg-[var(--surface-muted)] text-[var(--text-secondary)] hover:bg-[var(--surface-panel-raised)] hover:text-[var(--text-primary)]"
                  }`}
                >
                  {p}
                </button>
              ))}
            </div>
          </div>
        </header>

        <LiveStatusStrip summary={statusSummary} />

        <section className="space-y-4">
          {loading && !data ? <LoadingBlock message={`Loading ${page.toLowerCase()}...`} /> : null}
          {!loading && error && !data ? <ErrorState title="Unable to load page data" message={error} onRetry={load} /> : null}
          {!loading && page === "Dashboard" && data ? <Dashboard data={data} summary={statusSummary} goToPage={setPage} /> : null}
          {!loading && page === "Dashboard" && !data && !error ? <EmptyState title="No dashboard data" message="No dashboard payload was returned by the API." /> : null}
          {!loading && page === "Activity" ? <ActivityTimeline rows={Array.isArray(data) ? data : []} /> : null}
          {!loading && page === "Players" ? <PlayersTable rows={Array.isArray(data) ? data : []} /> : null}
          {!loading && page === "Promotions" ? <PromotionsTable rows={Array.isArray(data) ? data : []} action={action} busy={busy} /> : null}
          {!loading && page === "Review" ? <ReviewTable rows={Array.isArray(data) ? data : []} action={action} busy={busy} /> : null}
          {!loading && page === "Settings" && data ? <SettingsTable data={data} /> : null}
          {!loading && page === "Settings" && !data && !error ? <EmptyState title="No settings available" message="The settings endpoint returned no data." /> : null}
        </section>
      </div>
    </main>
  );
}

function LiveStatusStrip({ summary }) {
  return (
    <section className="rounded-2xl border border-[var(--border-subtle)] bg-[var(--surface-panel)] p-4 sm:p-5">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div className="flex items-center gap-2">
          <span className={`h-2.5 w-2.5 rounded-full ${chipDotClasses[summary.tone] ?? chipDotClasses.neutral}`} />
          <div>
            <p className="text-sm font-semibold text-[var(--text-primary)]">{summary.title}</p>
            <p className="text-xs text-[var(--text-secondary)]">{summary.subtitle}</p>
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          {summary.latestSync ? <ToneChip tone="neutral">Latest player: {summary.latestSync.currentPlayer ?? "Unknown"}</ToneChip> : null}
          {summary.recentEvent ? <ToneChip tone="sync">Recent event: {summary.recentEvent.state}</ToneChip> : null}
          {!summary.hasData ? <ToneChip tone="warning">Heartbeat missing</ToneChip> : null}
        </div>
      </div>
      {summary.workerComponents?.length ? (
        <div className="mt-4 border-t border-[var(--border-subtle)] pt-4">
          <div className="flex flex-wrap gap-1.5">
          {summary.workerComponents.map((item) => (
            <ToneChip key={item.component} tone={toneFromComponent(item)}>
              {item.component}: {item.state}
            </ToneChip>
          ))}
          </div>
        </div>
      ) : null}
    </section>
  );
}

function Dashboard({ data, summary, goToPage }) {
  const urgent = [];
  if ((data.pendingPromotions ?? 0) > 0) urgent.push({ label: `${data.pendingPromotions} promotions pending`, page: "Promotions", tone: "warning" });
  if ((data.pendingReview ?? 0) > 0) urgent.push({ label: `${data.pendingReview} players need review`, page: "Review", tone: "warning" });
  if ((data.missing ?? 0) > 0) urgent.push({ label: `${data.missing} players marked missing`, page: "Players", tone: "danger" });

  return (
    <div className="space-y-5">
      <div className="grid gap-3 xl:grid-cols-12">
        <StatCard
          label="Total Players"
          value={data.players ?? 0}
          hint="Tracked in current clan roster"
          className="xl:col-span-5"
        />
        <div className="grid gap-3 sm:grid-cols-3 xl:col-span-7">
          <StatCard label="Pending Promotions" value={data.pendingPromotions ?? 0} tone={(data.pendingPromotions ?? 0) > 0 ? "warning" : "success"} hint="Approval queue" />
          <StatCard label="Pending Review" value={data.pendingReview ?? 0} tone={(data.pendingReview ?? 0) > 0 ? "warning" : "success"} hint="Needs operator decision" />
          <StatCard label="Missing Players" value={data.missing ?? 0} tone={(data.missing ?? 0) > 0 ? "danger" : "success"} hint="Potential roster drift" />
        </div>
      </div>

      <div className="grid gap-4 xl:grid-cols-[1.3fr_1fr]">
        <SectionCard title="Action Required" subtitle="Handle urgent queues before routine maintenance.">
          {urgent.length ? (
            <div className="space-y-2">
              {urgent.map((item) => (
                <div key={item.label} className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2">
                  <ToneChip tone={item.tone}>{item.tone === "danger" ? "High risk" : "Pending"}</ToneChip>
                  <span className="text-sm text-[var(--text-primary)]">{item.label}</span>
                  <button
                    onClick={() => goToPage(item.page)}
                    className="min-h-11 rounded-md border border-[var(--primary-accent-border)] bg-[var(--primary-accent)] px-3 py-2 text-xs font-semibold text-[var(--surface-canvas)] hover:bg-[var(--primary-accent-hover)]"
                  >
                    Open {item.page}
                  </button>
                </div>
              ))}
            </div>
          ) : (
            <EmptyState title="No urgent actions" message="Tracker queues are currently stable." />
          )}
        </SectionCard>

        <SectionCard title="Health Snapshot" subtitle="Worker and sync telemetry from live status.">
          <div className="space-y-2 text-sm text-[var(--text-secondary)]">
            <div className="rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2">
              <p className="text-xs uppercase tracking-wide text-[var(--text-muted)]">Current worker state</p>
              <p className="mt-1 text-[var(--text-primary)]">{summary.currentWorker?.state ?? "No worker state"}</p>
            </div>
            <div className="rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2">
              <p className="text-xs uppercase tracking-wide text-[var(--text-muted)]">Latest synced player</p>
              <p className="mt-1 text-[var(--text-primary)]">{summary.latestSync?.currentPlayer ?? "No player synced yet"}</p>
              <p className="mt-1 text-xs">{summary.latestSync ? `${fmt(summary.latestSync.heartbeatAt)} (${rel(summary.latestSync.heartbeatAt)})` : "Waiting for first sync heartbeat."}</p>
            </div>
            <div className="rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2">
              <p className="text-xs uppercase tracking-wide text-[var(--text-muted)]">Recent event</p>
              <p className="mt-1 text-[var(--text-primary)]">{summary.recentEvent ? `${summary.recentEvent.currentPlayer ? `${summary.recentEvent.currentPlayer}: ` : ""}${summary.recentEvent.state}` : "No recent event"}</p>
            </div>
          </div>
        </SectionCard>
      </div>
    </div>
  );
}

function FilterInput({ value, onChange, placeholder = "Filter..." }) {
  return (
    <input
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      className="min-h-11 w-full rounded-md border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2 text-sm text-[var(--text-primary)] outline-none transition focus:border-[var(--status-sync-text)]"
    />
  );
}

function SortHeader({ label, field, sort, setSort }) {
  const active = sort.field === field;
  const direction = active ? sort.dir : null;
  const sortLabel = active ? `${label}, sorted ${direction === "asc" ? "ascending" : "descending"}` : `${label}, not sorted`;

  return (
    <button
      type="button"
      className="inline-flex items-center gap-1 text-left font-semibold text-[var(--text-secondary)] hover:text-[var(--text-primary)]"
      aria-label={sortLabel}
      aria-pressed={active}
      onClick={() => setSort(active ? { field, dir: direction === "asc" ? "desc" : "asc" } : { field, dir: "asc" })}
    >
      {label}
      {active ? <span className="text-xs text-[var(--text-muted)]">{direction === "asc" ? "^" : "v"}</span> : null}
    </button>
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

  const toneForRow = (row) => {
    if (row.status === "OPEN") return "warning";
    if ((row.eventType ?? "").toLowerCase().includes("fail") || (row.status ?? "").toLowerCase().includes("error")) return "danger";
    return "success";
  };

  return (
    <div className="space-y-4">
      <SectionCard
        title="Filters"
        subtitle="Focus the timeline to the signals that matter right now."
        right={<div className="w-full sm:w-72"><FilterInput value={query} onChange={setQuery} placeholder="Search activity" /></div>}
      >
        <div className="flex flex-wrap gap-1">
          {activityFilters.map((item) => (
            <button
              key={item.key}
              onClick={() => setFilter(item.key)}
              className={`min-h-11 rounded-md border px-2.5 py-2 text-xs font-semibold transition ${
                filter === item.key
                  ? "border-[var(--primary-accent-border)] bg-[var(--primary-accent)] text-[var(--surface-canvas)]"
                  : "border-[var(--border-subtle)] bg-[var(--surface-muted)] text-[var(--text-secondary)] hover:bg-[var(--surface-panel-raised)]"
              }`}
            >
              {item.label} <span className="opacity-80">{counts[item.key] ?? 0}</span>
            </button>
          ))}
        </div>
      </SectionCard>

      {filtered.length === 0 ? (
        <EmptyState title="No activity found" message="Try a different filter or search query." />
      ) : (
        <div className="overflow-hidden rounded-xl border border-[var(--border-subtle)]">
          {filtered.map((row) => (
            <div key={row.id} className="grid gap-2 border-b border-[var(--border-subtle)] bg-[var(--surface-panel)] p-3 last:border-b-0 md:grid-cols-[180px_1fr]">
              <div className="text-xs text-[var(--text-muted)]">
                <div>{fmt(row.createdAt)}</div>
                <div>{rel(row.createdAt)}</div>
              </div>
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-1.5">
                  <ToneChip tone="neutral">{row.categoryLabel}</ToneChip>
                  <ToneChip tone={toneForRow(row)}>{row.status}</ToneChip>
                  {row.actor ? <ToneChip tone="sync">By {row.actor}</ToneChip> : null}
                  <span className="text-xs text-[var(--text-muted)]">{row.eventType}</span>
                </div>
                <p className="mt-1 text-sm font-semibold text-[var(--text-primary)]">{row.title}</p>
                <p className="mt-0.5 text-sm text-[var(--text-secondary)]">{row.description}</p>
                {row.details?.length ? (
                  <div className="mt-2 flex flex-wrap gap-1.5">
                    {row.details.slice(0, 8).map((detail) => (
                      <span key={`${row.id}-${detail.label}`} className="rounded-md border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-2 py-0.5 text-xs text-[var(--text-secondary)]">
                        <span className="font-medium text-[var(--text-primary)]">{displayDetailLabel(detail.label)}:</span> {fmtDetail(detail.label, detail.value)}
                      </span>
                    ))}
                  </div>
                ) : null}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
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

  if (!rows.length) {
    return <EmptyState title="No players returned" message="The player endpoint returned an empty roster." />;
  }

  return (
    <SectionCard title="Roster Table" subtitle="Dense, sortable member state for operations.">
      <div className="space-y-2 md:hidden">
        {sorted.map((r) => {
          const pets = r.manualPetOverride ?? r.storedPetCount;
          const stale = isOlderThanHours(r.lastSynced, 12);
          return (
            <article key={r.id} className="rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] p-3">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <p className="truncate text-sm font-semibold text-[var(--text-primary)]">{r.username}</p>
                  <p className="text-xs text-[var(--text-secondary)]">Current: {r.currentRank ?? "-"}</p>
                </div>
                <ToneChip tone={r.status?.includes("MISSING") ? "danger" : r.status?.includes("REVIEW") ? "warning" : "success"}>{r.status}</ToneChip>
              </div>
              <div className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 text-xs text-[var(--text-secondary)]">
                <span>Total: {r.totalLevel ?? "N/A"}</span>
                <span>EHB: {r.ehb != null ? Number(r.ehb).toFixed(1) : "N/A"}</span>
                <span>EHP: {r.ehp != null ? Number(r.ehp).toFixed(1) : "N/A"}</span>
                <span>Pets: {pets > 0 ? pets : "N/A"}</span>
              </div>
              <div className="mt-2 text-xs text-[var(--text-secondary)]">
                <div>{fmt(r.lastSynced)}</div>
                <div className={stale ? "text-[var(--status-warning-soft)]" : "text-[var(--text-muted)]"}>{rel(r.lastSynced)}</div>
              </div>
            </article>
          );
        })}
      </div>

      <div className="hidden overflow-auto rounded-lg border border-[var(--border-subtle)] md:block">
        <table className="min-w-full text-sm">
          <thead className="sticky top-0 z-10 bg-[var(--surface-panel-raised)]">
            <tr className="border-b border-[var(--border-subtle)] text-left">
              <th className="p-2"><SortHeader label="Username" field="username" sort={sort} setSort={setSort} /></th>
              <th className="p-2"><SortHeader label="Current" field="currentRank" sort={sort} setSort={setSort} /></th>
              <th className="p-2"><SortHeader label="Status" field="status" sort={sort} setSort={setSort} /></th>
              <th className="p-2 text-right"><SortHeader label="Total" field="totalLevel" sort={sort} setSort={setSort} /></th>
              <th className="p-2 text-right"><SortHeader label="EHB" field="ehb" sort={sort} setSort={setSort} /></th>
              <th className="p-2 text-right"><SortHeader label="EHP" field="ehp" sort={sort} setSort={setSort} /></th>
              <th className="p-2 text-right"><SortHeader label="Pets" field="pets" sort={sort} setSort={setSort} /></th>
              <th className="p-2"><SortHeader label="Last Synced" field="lastSynced" sort={sort} setSort={setSort} /></th>
            </tr>
          </thead>
          <tbody>
            {sorted.map((r) => {
              const pets = r.manualPetOverride ?? r.storedPetCount;
              const stale = isOlderThanHours(r.lastSynced, 12);
              return (
                <tr key={r.id} className="border-b border-[var(--border-subtle)] align-top odd:bg-[var(--surface-panel)] even:bg-[var(--surface-muted)] hover:bg-[var(--surface-panel-raised)]">
                  <td className="p-2 font-medium text-[var(--text-primary)]">{r.username}</td>
                  <td className="p-2 text-[var(--text-secondary)]">{r.currentRank ?? "-"}</td>
                  <td className="p-2">
                    <ToneChip tone={r.status?.includes("MISSING") ? "danger" : r.status?.includes("REVIEW") ? "warning" : "success"}>{r.status}</ToneChip>
                  </td>
                  <td className="p-2 text-right text-[var(--text-secondary)]">{r.totalLevel ?? "N/A"}</td>
                  <td className="p-2 text-right text-[var(--text-secondary)]">{r.ehb != null ? Number(r.ehb).toFixed(1) : "N/A"}</td>
                  <td className="p-2 text-right text-[var(--text-secondary)]">{r.ehp != null ? Number(r.ehp).toFixed(1) : "N/A"}</td>
                  <td className="p-2 text-right text-[var(--text-secondary)]">{pets > 0 ? pets : "N/A"}</td>
                  <td className="p-2 text-[var(--text-secondary)]">
                    <div>{fmt(r.lastSynced)}</div>
                    <div className={`text-xs ${stale ? "text-[var(--status-warning-soft)]" : "text-[var(--text-muted)]"}`}>{rel(r.lastSynced)}</div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </SectionCard>
  );
}

function PromotionsTable({ rows, action, busy }) {
  const [sort, setSort] = useState({ field: "createdAt", dir: "desc" });
  const [approveAllArmed, setApproveAllArmed] = useState(false);

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
    await action(() => call("/promotions/approve-all", { method: "POST" }));
    setApproveAllArmed(false);
  }

  return (
    <div className="space-y-3">
      <SectionCard
        title="Bulk Action"
        subtitle="High-impact approval operation. Confirm before executing."
        right={
          <div className="flex flex-wrap items-center gap-2">
            {approveAllArmed ? (
              <>
                <button
                  disabled={busy}
                  onClick={approveAll}
                  className="min-h-11 rounded-lg border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-danger-text)] transition hover:bg-[var(--surface-panel-raised)] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {busy ? "Processing..." : `Confirm approve all (${rows.length})`}
                </button>
                <button
                  disabled={busy}
                  onClick={() => setApproveAllArmed(false)}
                  className="min-h-11 rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] transition hover:bg-[var(--surface-panel-raised)] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  Cancel
                </button>
              </>
            ) : (
              <button
                disabled={busy || rows.length === 0}
                onClick={() => setApproveAllArmed(true)}
                className="min-h-11 rounded-lg border border-[var(--status-warning-border)] bg-[var(--status-warning-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-warning-text)] transition hover:bg-[var(--primary-accent-soft)] disabled:cursor-not-allowed disabled:opacity-50"
              >
                Arm bulk approval
              </button>
            )}
          </div>
        }
      >
        {rows.length === 0 ? <EmptyState title="No pending promotions" message="Promotion queue is currently empty." /> : null}
        {approveAllArmed && rows.length > 0 ? (
          <p className="mt-2 text-xs text-[var(--status-warning-soft)]">Bulk approval is armed. Confirm to post all pending promotions.</p>
        ) : null}
      </SectionCard>

      {rows.length ? (
        <div className="space-y-2 md:hidden">
          {sorted.map((r) => (
            <article key={r.id} className="rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] p-3">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <p className="truncate text-sm font-semibold text-[var(--text-primary)]">{r.username}</p>
                  <p className="text-xs text-[var(--text-secondary)]">{r.oldRank} {"->"} {r.newRank}</p>
                </div>
                <ToneChip tone="warning">Pending</ToneChip>
              </div>
              <p className="mt-2 text-xs text-[var(--text-secondary)]">{candidateTypeLabel(r.candidateType)}</p>
              <p className="mt-1 break-words text-xs text-[var(--text-secondary)]">{r.reason}</p>
              <p className="mt-1 text-xs text-[var(--text-muted)]">Created {fmt(r.createdAt)} ({rel(r.createdAt)})</p>
              <div className="mt-3 flex flex-wrap gap-2">
                <button
                  disabled={busy}
                  className="min-h-11 rounded-md border border-[var(--status-success-border)] bg-[var(--status-success-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-success-text)] disabled:cursor-not-allowed disabled:opacity-50"
                  onClick={() => action(() => call(`/promotions/${r.id}/approve`, { method: "POST" }))}
                >
                  Approve
                </button>
                <button
                  disabled={busy}
                  className="min-h-11 rounded-md border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-danger-text)] disabled:cursor-not-allowed disabled:opacity-50"
                  onClick={() => action(() => call(`/promotions/${r.id}/dismiss`, { method: "POST" }))}
                >
                  Dismiss
                </button>
              </div>
            </article>
          ))}
        </div>
      ) : null}

      {rows.length ? (
        <div className="hidden overflow-auto rounded-lg border border-[var(--border-subtle)] md:block">
          <table className="min-w-full text-sm">
            <thead className="sticky top-0 z-10 bg-[var(--surface-panel-raised)]">
              <tr className="border-b border-[var(--border-subtle)] text-left">
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
              {sorted.map((r) => (
                <tr key={r.id} className="border-b border-[var(--border-subtle)] align-top odd:bg-[var(--surface-panel)] even:bg-[var(--surface-muted)] hover:bg-[var(--surface-panel-raised)]">
                  <td className="p-2 font-medium text-[var(--text-primary)]">{r.username}</td>
                  <td className="p-2 text-[var(--text-secondary)]">{r.oldRank}</td>
                  <td className="p-2 text-[var(--text-secondary)]">{r.newRank}</td>
                  <td className="p-2 text-[var(--text-secondary)]">{candidateTypeLabel(r.candidateType)}</td>
                  <td className="max-w-xl break-words p-2 text-[var(--text-secondary)]">{r.reason}</td>
                  <td className="p-2 text-[var(--text-secondary)]">{fmt(r.createdAt)}</td>
                  <td className="p-2">
                    <div className="flex flex-wrap gap-2">
                      <button
                        disabled={busy}
                        className="min-h-11 rounded-md border border-[var(--status-success-border)] bg-[var(--status-success-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-success-text)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => action(() => call(`/promotions/${r.id}/approve`, { method: "POST" }))}
                      >
                        Approve
                      </button>
                      <button
                        disabled={busy}
                        className="min-h-11 rounded-md border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-danger-text)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => action(() => call(`/promotions/${r.id}/dismiss`, { method: "POST" }))}
                      >
                        Dismiss
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </div>
  );
}

function ReviewTable({ rows, action, busy }) {
  const [sort, setSort] = useState({ field: "lastSeen", dir: "desc" });
  const [mergeDrafts, setMergeDrafts] = useState({});

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

  const openMergeDraft = (id, mode, value = "") => {
    setMergeDrafts((prev) => ({ ...prev, [id]: { mode, value } }));
  };

  const closeMergeDraft = (id) => {
    setMergeDrafts((prev) => {
      const next = { ...prev };
      delete next[id];
      return next;
    });
  };

  if (!rows.length) {
    return <EmptyState title="No review queue items" message="Review queue is currently clear." />;
  }

  return (
    <>
      <div className="space-y-2 md:hidden">
        {sorted.map((r) => {
          let mergeMeta = null;
          try {
            mergeMeta = r.mergeMetadataJson ? JSON.parse(r.mergeMetadataJson) : null;
          } catch {
            mergeMeta = null;
          }

          const suggestedPrevious = mergeMeta?.SuggestedPrevious || "";
          const candidatePreviousPlayers = Array.isArray(mergeMeta?.CandidatePreviousPlayers) ? mergeMeta.CandidatePreviousPlayers : [];
          const candidateNames = candidatePreviousPlayers.map((x) => x.PreviousPlayer).filter(Boolean);
          const isMissingReview = r.status === "MISSING_PENDING_REVIEW" || r.status === "NEW_PENDING_REVIEW";
          const isMerge = r.status === "MERGE_SUGGESTED";
          const draft = mergeDrafts[r.id] ?? null;

          return (
            <article key={`mobile-${r.id}`} className="rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-muted)] p-3">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <p className="truncate text-sm font-semibold text-[var(--text-primary)]">{r.username}</p>
                  <p className="text-xs text-[var(--text-secondary)]">{r.currentRank ?? "-"} {"->"} {r.eligibleRank ?? "-"}</p>
                </div>
                <ToneChip tone={isMerge || isMissingReview ? "warning" : "neutral"}>{r.status}</ToneChip>
              </div>
              <div className="mt-2 text-xs text-[var(--text-secondary)]">
                <p>Seen {fmt(r.lastSeen)} ({rel(r.lastSeen)})</p>
                <p>Discord card: {r.discordCardState ?? "-"}</p>
              </div>
              {isMissingReview ? (
                <div className="mt-3 flex flex-wrap gap-2">
                  <button
                    disabled={busy}
                    className="min-h-11 rounded-md border border-[var(--status-success-border)] bg-[var(--status-success-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-success-text)] disabled:cursor-not-allowed disabled:opacity-50"
                    onClick={() => action(() => call(`/review/players/${r.id}/temple-missing/add`, { method: "POST" }))}
                  >
                    Add to Temple
                  </button>
                  <button
                    disabled={busy}
                    className="min-h-11 rounded-md border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-danger-text)] disabled:cursor-not-allowed disabled:opacity-50"
                    onClick={() => action(() => call(`/review/players/${r.id}/temple-missing/remove-db`, { method: "POST" }))}
                  >
                    Remove from DB
                  </button>
                  <button
                    disabled={busy}
                    className="min-h-11 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-panel)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] disabled:cursor-not-allowed disabled:opacity-50"
                    onClick={() => action(() => call(`/review/players/${r.id}/requeue-discord-card`, { method: "POST" }))}
                  >
                    Requeue Card
                  </button>
                </div>
              ) : null}

              {isMerge ? (
                <div className="mt-3 flex flex-wrap gap-2">
                  <button
                    disabled={busy}
                    className="min-h-11 rounded-md border border-[var(--status-success-border)] bg-[var(--status-success-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-success-text)] disabled:cursor-not-allowed disabled:opacity-50"
                    onClick={() => action(() => call(`/review/players/${r.id}/merge/confirm`, { method: "POST" }))}
                  >
                    Confirm rename
                  </button>
                  <button
                    disabled={busy || candidateNames.length === 0}
                    className="min-h-11 rounded-md border border-[var(--status-sync-border)] bg-[var(--status-sync-bg-soft)] px-3 py-2 text-xs font-semibold text-[var(--status-sync-text-soft)] disabled:cursor-not-allowed disabled:opacity-50"
                    onClick={() => openMergeDraft(r.id, "pick", suggestedPrevious || candidateNames[0] || "")}
                  >
                    Pick other
                  </button>
                  <button
                    disabled={busy}
                    className="min-h-11 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-panel)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] disabled:cursor-not-allowed disabled:opacity-50"
                    onClick={() => openMergeDraft(r.id, "manual", suggestedPrevious || "")}
                  >
                    Manual previous
                  </button>
                  <button
                    disabled={busy}
                    className="min-h-11 rounded-md border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-danger-text)] disabled:cursor-not-allowed disabled:opacity-50"
                    onClick={() => action(() => call(`/review/players/${r.id}/merge/abort`, { method: "POST" }))}
                  >
                    Abort rename
                  </button>
                </div>
              ) : null}

              {isMerge && draft?.mode === "pick" ? (
                <div className="mt-2 rounded-md border border-[var(--status-sync-border)] bg-[var(--status-sync-bg-soft)] p-2">
                  <label className="block text-xs font-semibold text-[var(--status-sync-text-soft)]" htmlFor={`pick-card-${r.id}`}>Choose previous player</label>
                  <select
                    id={`pick-card-${r.id}`}
                    value={draft.value}
                    disabled={busy}
                    onChange={(e) => openMergeDraft(r.id, "pick", e.target.value)}
                    className="mt-1 min-h-11 w-full rounded-md border border-[var(--status-sync-border)] bg-[var(--status-sync-bg-soft)] px-2 py-2 text-sm text-[var(--status-sync-text-soft)]"
                  >
                    {candidateNames.map((name) => <option key={name} value={name}>{name}</option>)}
                  </select>
                  <div className="mt-2 flex flex-wrap gap-2">
                    <button
                      disabled={busy || !draft.value}
                      onClick={async () => {
                        await action(() => call(`/review/players/${r.id}/merge/reassign`, { method: "POST", body: JSON.stringify({ previousUsername: draft.value }) }));
                        closeMergeDraft(r.id);
                      }}
                      className="min-h-11 rounded-md border border-[var(--status-sync-border)] bg-[var(--status-sync-bg-soft)] px-3 py-2 text-xs font-semibold text-[var(--status-sync-text-soft)] disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      Apply selection
                    </button>
                    <button
                      disabled={busy}
                      onClick={() => closeMergeDraft(r.id)}
                      className="min-h-11 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-panel)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : null}

              {isMerge && draft?.mode === "manual" ? (
                <div className="mt-2 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-panel)] p-2">
                  <label className="block text-xs font-semibold text-[var(--text-secondary)]" htmlFor={`manual-card-${r.id}`}>Manual previous username</label>
                  <input
                    id={`manual-card-${r.id}`}
                    type="text"
                    maxLength={64}
                    value={draft.value}
                    disabled={busy}
                    onChange={(e) => openMergeDraft(r.id, "manual", e.target.value)}
                    className="mt-1 min-h-11 w-full rounded-md border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-2 py-2 text-sm text-[var(--text-primary)]"
                  />
                  <div className="mt-2 flex flex-wrap gap-2">
                    <button
                      disabled={busy || !draft.value.trim()}
                      onClick={async () => {
                        await action(() => call(`/review/players/${r.id}/merge/manual`, { method: "POST", body: JSON.stringify({ previousUsername: draft.value.trim() }) }));
                        closeMergeDraft(r.id);
                      }}
                      className="min-h-11 rounded-md border border-[var(--status-success-border)] bg-[var(--status-success-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-success-text)] disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      Apply manual name
                    </button>
                    <button
                      disabled={busy}
                      onClick={() => closeMergeDraft(r.id)}
                      className="min-h-11 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-panel-raised)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : null}
            </article>
          );
        })}
      </div>

      <div className="hidden overflow-auto rounded-lg border border-[var(--border-subtle)] md:block">
      <table className="min-w-full text-sm">
        <thead className="sticky top-0 z-10 bg-[var(--surface-panel-raised)]">
          <tr className="border-b border-[var(--border-subtle)] text-left">
            <th className="p-2"><SortHeader label="Username" field="username" sort={sort} setSort={setSort} /></th>
            <th className="p-2"><SortHeader label="Status" field="status" sort={sort} setSort={setSort} /></th>
            <th className="p-2"><SortHeader label="Current" field="currentRank" sort={sort} setSort={setSort} /></th>
            <th className="p-2"><SortHeader label="Eligible" field="eligibleRank" sort={sort} setSort={setSort} /></th>
            <th className="p-2"><SortHeader label="Last Seen" field="lastSeen" sort={sort} setSort={setSort} /></th>
            <th className="p-2">Discord Card</th>
            <th className="p-2">Review Actions</th>
          </tr>
        </thead>
        <tbody>
          {sorted.map((r) => {
            let mergeMeta = null;
            try {
              mergeMeta = r.mergeMetadataJson ? JSON.parse(r.mergeMetadataJson) : null;
            } catch {
              mergeMeta = null;
            }

            const suggestedPrevious = mergeMeta?.SuggestedPrevious || "";
            const candidatePreviousPlayers = Array.isArray(mergeMeta?.CandidatePreviousPlayers) ? mergeMeta.CandidatePreviousPlayers : [];
            const candidateNames = candidatePreviousPlayers.map((x) => x.PreviousPlayer).filter(Boolean);
            const isMissingReview = r.status === "MISSING_PENDING_REVIEW" || r.status === "NEW_PENDING_REVIEW";
            const isMerge = r.status === "MERGE_SUGGESTED";
            const draft = mergeDrafts[r.id] ?? null;

            return (
              <tr key={r.id} className="border-b border-[var(--border-subtle)] align-top odd:bg-[var(--surface-panel)] even:bg-[var(--surface-muted)] hover:bg-[var(--surface-panel-raised)]">
                <td className="max-w-[220px] break-words p-2 font-medium text-[var(--text-primary)]">{r.username}</td>
                <td className="p-2"><ToneChip tone={isMerge || isMissingReview ? "warning" : "neutral"}>{r.status}</ToneChip></td>
                <td className="p-2 text-[var(--text-secondary)]">{r.currentRank ?? "-"}</td>
                <td className="p-2 text-[var(--text-secondary)]">{r.eligibleRank ?? "-"}</td>
                <td className="p-2 text-[var(--text-secondary)]">
                  <div>{fmt(r.lastSeen)}</div>
                  <div className="text-xs text-[var(--text-muted)]">{rel(r.lastSeen)}</div>
                </td>
                <td className="p-2 text-[var(--text-secondary)]">{r.discordCardState ?? "-"}</td>
                <td className="p-2">
                  {isMissingReview ? (
                    <div className="flex flex-wrap gap-2">
                      <button
                        disabled={busy}
                        className="min-h-11 rounded-md border border-[var(--status-success-border)] bg-[var(--status-success-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-success-text)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => action(() => call(`/review/players/${r.id}/temple-missing/add`, { method: "POST" }))}
                      >
                        Add to Temple
                      </button>
                      <button
                        disabled={busy}
                        className="min-h-11 rounded-md border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-danger-text)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => action(() => call(`/review/players/${r.id}/temple-missing/remove-db`, { method: "POST" }))}
                      >
                        Remove from DB
                      </button>
                      <button
                        disabled={busy}
                        className="min-h-11 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => action(() => call(`/review/players/${r.id}/requeue-discord-card`, { method: "POST" }))}
                      >
                        Requeue Card
                      </button>
                    </div>
                  ) : null}

                  {isMerge ? (
                    <div className="flex flex-wrap gap-2">
                      <button
                        disabled={busy}
                        className="min-h-11 rounded-md border border-[var(--status-success-border)] bg-[var(--status-success-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-success-text)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => action(() => call(`/review/players/${r.id}/merge/confirm`, { method: "POST" }))}
                      >
                        Confirm rename
                      </button>
                      <button
                        disabled={busy || candidateNames.length === 0}
                        className="min-h-11 rounded-md border border-[var(--status-sync-border)] bg-[var(--status-sync-bg-soft)] px-3 py-2 text-xs font-semibold text-[var(--status-sync-text-soft)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => openMergeDraft(r.id, "pick", suggestedPrevious || candidateNames[0] || "")}
                      >
                        Pick other
                      </button>
                      <button
                        disabled={busy}
                        className="min-h-11 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => openMergeDraft(r.id, "manual", suggestedPrevious || "")}
                      >
                        Manual previous
                      </button>
                      <button
                        disabled={busy}
                        className="min-h-11 rounded-md border border-[var(--status-danger-border)] bg-[var(--status-danger-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-danger-text)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => action(() => call(`/review/players/${r.id}/merge/abort`, { method: "POST" }))}
                      >
                        Abort rename
                      </button>
                      <button
                        disabled={busy}
                        className="min-h-11 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] disabled:cursor-not-allowed disabled:opacity-50"
                        onClick={() => action(() => call(`/review/players/${r.id}/requeue-discord-card`, { method: "POST" }))}
                      >
                        Requeue Card
                      </button>
                    </div>
                  ) : null}

                  {isMerge && draft?.mode === "pick" ? (
                    <div className="mt-2 rounded-md border border-[var(--status-sync-border)] bg-[var(--status-sync-bg-soft)] p-2">
                      <label className="block text-xs font-semibold text-[var(--status-sync-text-soft)]" htmlFor={`pick-table-${r.id}`}>Choose previous player</label>
                      <select
                        id={`pick-table-${r.id}`}
                        value={draft.value}
                        disabled={busy}
                        onChange={(e) => openMergeDraft(r.id, "pick", e.target.value)}
                        className="mt-1 min-h-11 w-full rounded-md border border-[var(--status-sync-border)] bg-[var(--status-sync-bg-soft)] px-2 py-2 text-sm text-[var(--status-sync-text-soft)]"
                      >
                        {candidateNames.map((name) => <option key={name} value={name}>{name}</option>)}
                      </select>
                      <div className="mt-2 flex flex-wrap gap-2">
                        <button
                          disabled={busy || !draft.value}
                          onClick={async () => {
                            await action(() => call(`/review/players/${r.id}/merge/reassign`, { method: "POST", body: JSON.stringify({ previousUsername: draft.value }) }));
                            closeMergeDraft(r.id);
                          }}
                          className="min-h-11 rounded-md border border-[var(--status-sync-border)] bg-[var(--status-sync-bg-soft)] px-3 py-2 text-xs font-semibold text-[var(--status-sync-text-soft)] disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          Apply selection
                        </button>
                        <button
                          disabled={busy}
                          onClick={() => closeMergeDraft(r.id)}
                          className="min-h-11 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-muted)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  ) : null}

                  {isMerge && draft?.mode === "manual" ? (
                    <div className="mt-2 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-muted)] p-2">
                      <label className="block text-xs font-semibold text-[var(--text-secondary)]" htmlFor={`manual-table-${r.id}`}>Manual previous username</label>
                      <input
                        id={`manual-table-${r.id}`}
                        type="text"
                        maxLength={64}
                        value={draft.value}
                        disabled={busy}
                        onChange={(e) => openMergeDraft(r.id, "manual", e.target.value)}
                        className="mt-1 min-h-11 w-full rounded-md border border-[var(--border-subtle)] bg-[var(--surface-panel)] px-2 py-2 text-sm text-[var(--text-primary)]"
                      />
                      <div className="mt-2 flex flex-wrap gap-2">
                        <button
                          disabled={busy || !draft.value.trim()}
                          onClick={async () => {
                            await action(() => call(`/review/players/${r.id}/merge/manual`, { method: "POST", body: JSON.stringify({ previousUsername: draft.value.trim() }) }));
                            closeMergeDraft(r.id);
                          }}
                          className="min-h-11 rounded-md border border-[var(--status-success-border)] bg-[var(--status-success-bg)] px-3 py-2 text-xs font-semibold text-[var(--status-success-text)] disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          Apply manual name
                        </button>
                        <button
                          disabled={busy}
                          onClick={() => closeMergeDraft(r.id)}
                          className="min-h-11 rounded-md border border-[var(--border-subtle)] bg-[var(--surface-panel-raised)] px-3 py-2 text-xs font-semibold text-[var(--text-secondary)] disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  ) : null}

                  {!isMissingReview && !isMerge ? <span className="text-xs text-[var(--text-muted)]">No actions available</span> : null}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      </div>
    </>
  );
}

function SettingsTable({ data }) {
  return (
    <div className="grid gap-3 lg:grid-cols-2">
      <SectionCard title="API Rate Limit" subtitle="Temple API calls allowed per minute.">
        <p className="text-2xl font-semibold text-[var(--text-primary)]">{data.apiRateLimitPerMinute}</p>
      </SectionCard>
      <SectionCard title="Connection String" subtitle="Backend runtime connection readiness.">
        <ToneChip tone={data.connectionStringConfigured ? "success" : "danger"}>
          {data.connectionStringConfigured ? "Configured" : "Missing"}
        </ToneChip>
      </SectionCard>
    </div>
  );
}
