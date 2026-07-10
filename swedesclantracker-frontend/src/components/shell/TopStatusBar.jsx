import { StatusPill } from "../osrs/StatusPill";
import { cleanText, formatDisplayLabel } from "../../data/viewModels/formatters";

export function TopStatusBar({ home, liveStatus }) {
  const view = buildStatusView(home, liveStatus);

  return (
    <header className="osrs-topbar">
      <div className="osrs-topbar-status">
        <StatusPill tone={view.tone} loading={view.loading}>{view.label}</StatusPill>
        {view.detail ? <span>{view.detail}</span> : null}
      </div>
      <div className="osrs-topbar-session">Signed in</div>
    </header>
  );
}

function buildStatusView(home, liveStatus) {
  if (liveStatus?.loading && !liveStatus?.data) {
    return { label: "Tracker status loading", detail: "", tone: "info", loading: true };
  }

  if (liveStatus?.error && !liveStatus?.data) {
    return { label: "Tracker status unavailable", detail: liveStatus.error, tone: "danger" };
  }

  const components = Array.isArray(liveStatus?.data?.components) ? liveStatus.data.components : [];
  const worker = components.find((item) => {
    const component = cleanText(item?.component, "").toLowerCase();
    return component && component !== "api" && component !== "latest sync" && component !== "recent event";
  });
  const latest = components.find((item) => cleanText(item?.component, "").toLowerCase() === "latest sync");

  if (worker) {
    const tone = worker.isOffline ? "danger" : worker.isStale ? "warning" : "success";
    const detail = latest?.currentPlayer ? `Latest sync: ${latest.currentPlayer}` : cleanText(worker.message, "");
    return { label: `Tracker: ${formatDisplayLabel(worker.state, "Unknown")}`, detail, tone };
  }

  const health = home?.health;
  if (health) {
    const overall = cleanText(health.overall, "unknown");
    const tone = overall === "critical" ? "danger" : overall === "warning" ? "warning" : overall === "healthy" ? "success" : "neutral";
    const detail = health.sync?.lastPlayer ? `Latest sync: ${health.sync.lastPlayer} ${health.sync.syncedAgo ?? ""}`.trim() : "";
    return { label: `Tracker: ${formatDisplayLabel(overall)}`, detail, tone };
  }

  return { label: "Tracker status pending", detail: "", tone: "neutral" };
}
