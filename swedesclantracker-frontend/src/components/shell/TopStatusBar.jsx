import { StatusPill } from "../osrs/StatusPill";

export function TopStatusBar({ home, liveStatus }) {
  const status = buildStatusView(home, liveStatus);

  return (
    <header className="osrs-topbar">
      <div className="osrs-topbar-status">
        <StatusPill tone={status.tone} loading={status.loading}>{status.label}</StatusPill>
        {status.detail ? <span>{status.detail}</span> : null}
      </div>
      <div className="osrs-topbar-admin" aria-label="Current session">
        <span>Admin session</span>
      </div>
    </header>
  );
}

function buildStatusView(home, liveStatus) {
  if (liveStatus?.loading && !liveStatus?.data) {
    return {
      label: "Tracker status loading",
      detail: "",
      tone: "info",
      loading: true,
    };
  }

  if (liveStatus?.error && !liveStatus?.data) {
    return {
      label: "Tracker status unavailable",
      detail: liveStatus.error,
      tone: "danger",
    };
  }

  const components = Array.isArray(liveStatus?.data?.components) ? liveStatus.data.components : [];
  const latestSync = components.find((item) => item?.component === "Latest Sync");
  const worker = components.find((item) =>
    typeof item?.component === "string" &&
    item.component.toLowerCase() !== "api" &&
    item.component.toLowerCase() !== "latest sync" &&
    item.component.toLowerCase() !== "recent event");

  if (worker) {
    const isOffline = Boolean(worker.isOffline);
    const isStale = Boolean(worker.isStale);
    const tone = isOffline ? "danger" : isStale ? "warning" : "success";
    const label = isOffline ? "Worker offline" : isStale ? "Worker stale" : "Tracker stable";
    const detail = latestSync?.currentPlayer
      ? `Latest sync: ${latestSync.currentPlayer}`
      : worker.message ?? "";

    return { label, detail, tone };
  }

  const health = home?.health;
  if (health) {
    const overall = health.overall ?? "unknown";
    const tone = overall === "critical" ? "danger" : overall === "warning" ? "warning" : overall === "healthy" ? "success" : "info";
    const detail = health.sync?.lastPlayer
      ? `Latest sync: ${health.sync.lastPlayer} ${health.sync.syncedAgo ?? ""}`.trim()
      : health.worker?.lastHeartbeatAgo ? `Worker heartbeat: ${health.worker.lastHeartbeatAgo}` : "";

    return { label: `Tracker ${overall}`, detail, tone };
  }

  return {
    label: "Tracker status pending",
    detail: "",
    tone: "neutral",
  };
}
