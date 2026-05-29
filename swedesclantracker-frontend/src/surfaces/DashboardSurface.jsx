import { toneClass } from "../ui";

export function DashboardSurface({ data, liveStatus, loading, error, onRetry, onRetryLive, onOpenQueue }) {
  if (loading) {
    return <SurfaceMessage title="Dashboard" text="Loading dashboard overview..." tone="info" loading />;
  }

  if (error) {
    return (
      <SurfaceMessage
        title="Dashboard"
        text={`Unable to load dashboard: ${error}`}
        tone="danger"
        action={<button className="btn-ghost" onClick={onRetry}>Retry</button>}
      />
    );
  }

  if (!data) {
    return (
      <SurfaceMessage
        title="Dashboard"
        text="No dashboard data was returned by the API."
        tone="warning"
        action={<button className="btn-ghost" onClick={onRetry}>Retry</button>}
      />
    );
  }

  const apiTone = data.health.api?.state === "failed" ? "danger" : "success";
  const workerTone = data.health.worker?.state === "offline" ? "danger" : data.health.worker?.state === "stale" ? "warning" : "info";
  const live = buildLiveWorkerView(liveStatus?.data);
  const workerSummaryValue = live.hasLive ? live.label : (data.health.worker?.state ?? "unknown");
  const workerSummaryMeta = live.hasLive
    ? live.currentTask
    : `${data.health.worker?.currentPlayer ?? "unknown"} | ${data.health.worker?.lastHeartbeatAgo ?? "unknown"}`;
  const mergedLatestSync = live.hasLive
    ? live.latestSync
    : `${data.health.sync?.lastPlayer ?? "unknown"} | ${data.health.sync?.syncedAgo ?? "unknown"}`;
  const mergedLatestEvent = live.hasLive ? live.latestEvent : "No recent worker event yet";

  return (
    <div className="surface-grid">
      <header className="surface-header">
        <p className="eyebrow">Dashboard</p>
        <h2>Daily Clan and Tracker Overview</h2>
        <p>Health, roster posture, meaningful changes, and queued admin work in one daily brief.</p>
      </header>

      <section className="layout-two">
        <article className="panel">
          <h3>Tracker Health</h3>
          <div className="health-grid">
            <Health label="Overall" value={formatStatusLabel(data.health.overall)} tone={data.health.overall === "critical" ? "danger" : data.health.overall === "warning" ? "warning" : "success"} />
            <Health label="API" value={formatStatusLabel(data.health.api?.state)} meta={typeof data.health.api?.latencyMs === "number" && data.health.api.latencyMs > 0 ? `${data.health.api.latencyMs}ms` : "latency unavailable"} tone={apiTone} />
            <Health label="Worker" value={workerSummaryValue} meta={workerSummaryMeta} tone={live.hasLive ? live.tone : workerTone} />
            <Health label="Latest Sync" value={mergedLatestSync} tone="neutral" />
            <Health label="Latest Event" value={mergedLatestEvent} tone="info" />
          </div>

          {liveStatus?.loading && !liveStatus?.data ? <p className="tone tone-info" data-loading="true">Connecting to live status...</p> : null}
          {liveStatus?.error ? (
            <div className="live-status-note">
              <p className={liveStatus.stale ? "tone tone-warning" : "tone tone-danger"}>
                {liveStatus.stale ? "Showing last known worker status." : "Live worker status unavailable."}
              </p>
              <button className="btn-ghost" onClick={onRetryLive}>Retry live status</button>
            </div>
          ) : null}
        </article>

        <article className="panel">
          <h3>Work Waiting Preview</h3>
          {data.workPreview?.length ? (
            <ul className="stack-list">
              {data.workPreview.map((item) => (
                <li key={item.caseId} className="line-item">
                  <div>
                    <strong>{item.label}</strong>
                    <p>{item.caseId} | age {item.age ?? "unknown"}</p>
                  </div>
                  <span className={toneClass(item.risk === "high" ? "danger" : item.risk === "medium" ? "warning" : "success")}>{item.risk ?? "unknown"}</span>
                </li>
              ))}
            </ul>
          ) : (
            <p className="empty-note">No admin work waiting right now.</p>
          )}
          <button className="btn-primary" onClick={onOpenQueue}>Open Admin Queue</button>
        </article>
      </section>

      <section className="layout-two">
        <article className="panel">
          <h3>Roster Posture</h3>
          <div className="posture-grid">
            {(data.rosterPosture ?? []).map((item) => (
              <div key={item.label} className="posture-item">
                <small>{item.label}</small>
                <strong>{item.value ?? "-"}</strong>
                <span>{item.hint}</span>
                <i className={`dot dot-${item.tone ?? "neutral"}`} />
              </div>
            ))}
          </div>
        </article>

        <article className="panel">
          <h3>Progression Modules</h3>
          <div className="placeholder-stack">
            <Placeholder title="Rank progression" text="Planned slot for rank movement timeline and milestone comparisons." />
            <Placeholder title="Stat progression" text="Planned slot for EHP/EHB and level trend views." />
            <Placeholder title="Clan growth" text="Planned slot for membership growth and churn trends." />
          </div>
        </article>
      </section>

      <article className="panel">
        <h3>Recent Meaningful Clan Changes</h3>
        {data.meaningfulChanges?.length ? (
          <ul className="changes-list">
            {data.meaningfulChanges.map((change) => (
              <li key={change.id}>
                <span className={toneClass(change.tone ?? "info")}>{change.category ?? "system"}</span>
                <strong>{change.title}</strong>
                <small>{change.time ?? "unknown"}</small>
              </li>
            ))}
          </ul>
        ) : (
          <p className="empty-note">No meaningful clan changes recorded in the current window.</p>
        )}
      </article>
    </div>
  );
}

function Health({ label, value, meta, tone }) {
  const displayValue = typeof value === "string" && value.length ? value.charAt(0).toUpperCase() + value.slice(1) : value;
  return (
    <div className="health-item">
      <small>{label}</small>
      <strong>{displayValue}</strong>
      {meta ? <span>{meta}</span> : null}
      <i className={`dot dot-${tone}`} />
    </div>
  );
}

function Placeholder({ title, text }) {
  return (
    <div className="placeholder">
      <strong>{title}</strong>
      <p>{text}</p>
    </div>
  );
}

function SurfaceMessage({ title, text, tone, action = null, loading = false }) {
  return (
    <div className="surface-grid">
      <header className="surface-header">
        <p className="eyebrow">{title}</p>
      </header>
      <section className="panel">
        <p className={tone ? toneClass(tone) : "tone"} data-loading={loading ? "true" : undefined}>{text}</p>
        {action ? <div className="message-action">{action}</div> : null}
      </section>
    </div>
  );
}

function buildLiveWorkerView(payload) {
  if (!payload?.components?.length) {
    return {
      hasLive: false,
      tone: "warning",
      label: "Waiting for worker heartbeat",
      currentTask: "Waiting for worker heartbeat",
      latestSync: "No completed sync reported yet",
      latestEvent: "No recent worker event yet",
    };
  }

  const components = payload.components;
  const worker = components.find((item) =>
    typeof item?.component === "string" &&
    item.component.toLowerCase() !== "api" &&
    item.component.toLowerCase() !== "latest sync" &&
    item.component.toLowerCase() !== "recent event");
  const latestSync = components.find((item) => item?.component === "Latest Sync");
  const latestEvent = components.find((item) => item?.component === "Recent Event");

  const workerPlayer = worker?.currentPlayer?.trim() || "";
  const latestSyncPlayer = latestSync?.currentPlayer?.trim() || "";
  const stateRaw = (worker?.state ?? "").toLowerCase();
  const messageRaw = (worker?.message ?? "").toLowerCase();
  const detailsText = Object.values(worker?.details ?? {}).join(" ").toLowerCase();
  const isRateLimited = [stateRaw, messageRaw, detailsText].some((text) => text.includes("rate limit"));
  const isOffline = Boolean(worker?.isOffline);
  const isStale = Boolean(worker?.isStale);

  let label = "Worker idle";
  let tone = "info";
  if (!worker) {
    label = "Waiting for worker heartbeat";
    tone = "warning";
  } else if (isOffline) {
    label = "Worker offline";
    tone = "danger";
  } else if (isStale) {
    label = "Worker stale";
    tone = "warning";
  } else if (isRateLimited) {
    label = "Waiting for rate limit";
    tone = "warning";
  } else if (workerPlayer) {
    label = `Worker syncing ${workerPlayer}`;
    tone = "info";
  }

  let currentTask = workerPlayer ? `Syncing ${workerPlayer}` : (worker?.message ?? "Worker idle");
  if (isRateLimited && workerPlayer) {
    currentTask = `Awaiting rate limit after ${workerPlayer}`;
  } else if (isStale && workerPlayer) {
    currentTask = `Stalled on ${workerPlayer}`;
  } else if (!worker && !workerPlayer) {
    currentTask = "Waiting for worker heartbeat";
  }

  let latestSyncText = latestSyncPlayer
    ? `${latestSyncPlayer} (${humanizeAgeSeconds(latestSync?.ageSeconds)})`
    : "No completed sync reported yet";

  if (workerPlayer && latestSyncPlayer && workerPlayer === latestSyncPlayer) {
    latestSyncText = isRateLimited
      ? `Last completed sync also ${workerPlayer} (awaiting rate limit)`
      : `Still on ${workerPlayer} since latest completed sync`;
  }

  const latestEventText = latestEvent?.state
    ? `${latestEvent.state}${latestEvent.currentPlayer ? ` - ${latestEvent.currentPlayer}` : ""}`
    : "No recent worker event yet";

  return {
    hasLive: true,
    tone,
    label,
    currentTask,
    latestSync: latestSyncText,
    latestEvent: latestEventText,
  };
}

function humanizeAgeSeconds(seconds) {
  if (typeof seconds !== "number" || Number.isNaN(seconds)) return "unknown";
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

function formatStatusLabel(value) {
  if (typeof value !== "string" || !value.trim()) {
    return "Unknown";
  }

  return value
    .trim()
    .split(/[\s_-]+/)
    .filter(Boolean)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1).toLowerCase())
    .join(" ");
}
