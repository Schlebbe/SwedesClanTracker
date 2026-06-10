import { IconGlyph } from "../osrs/IconGlyph";
import { StatusPill } from "../osrs/StatusPill";

export function TopStatusBar({ home, liveStatus }) {
  const status = buildStatusView(home, liveStatus);

  return (
    <header className="osrs-topbar osrs-kenney-header-plate">
      <div className="osrs-topbar-status">
        <StatusPill tone={status.tone} loading={status.loading}>{status.label}</StatusPill>
        {status.detail ? <span className="osrs-topbar-sync">{status.detail}</span> : null}
      </div>

      <div className="osrs-topbar-search" role="search" aria-label="Member search preview">
        <span>Search members or hiscores...</span>
        <IconGlyph name="search" />
      </div>

      <div className="osrs-topbar-actions" aria-label="Session tools">
        <button type="button" className="osrs-topbar-icon-button" disabled title="Notifications are not wired yet.">
          <IconGlyph name="bell" />
        </button>
        <button type="button" className="osrs-topbar-icon-button" disabled title="Rank guard tools are not wired yet.">
          <IconGlyph name="shield" />
        </button>
        <div className="osrs-topbar-admin" aria-label="Current session">
          <span className="osrs-topbar-avatar" aria-hidden="true">
            <IconGlyph name="admin" />
          </span>
          <div>
            <strong>SwedesAdmin</strong>
            <span>Clan Leader</span>
          </div>
        </div>
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
    const label = isOffline ? "HiScores Sync: Offline" : isStale ? "HiScores Sync: Stale" : "HiScores Sync: Stable";
    const detail = latestSync?.currentPlayer
      ? `Last Snapshot: ${latestSync.currentPlayer}`
      : worker.message ?? "";

    return { label, detail, tone };
  }

  const health = home?.health;
  if (health) {
    const overall = health.overall ?? "unknown";
    const tone = overall === "critical" ? "danger" : overall === "warning" ? "warning" : overall === "healthy" ? "success" : "info";
    const detail = health.sync?.lastPlayer
      ? `Last Snapshot: ${health.sync.lastPlayer} ${health.sync.syncedAgo ?? ""}`.trim()
      : health.worker?.lastHeartbeatAgo ? `Worker heartbeat: ${health.worker.lastHeartbeatAgo}` : "";

    return { label: `HiScores Sync: ${formatStatusLabel(overall)}`, detail, tone };
  }

  return {
    label: "Tracker status pending",
    detail: "",
    tone: "neutral",
  };
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
