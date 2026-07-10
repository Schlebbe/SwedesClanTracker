import { cleanText, formatDisplayLabel, normalizeArray, statusTone } from "./formatters";

export function mapHomeToDashboardViewModel(home, liveStatus) {
  const overview = home?.overview ?? {};
  const health = home?.health ?? {};
  const posture = normalizeArray(home?.rosterPosture).map((item) => ({
    label: cleanText(item?.label, "Roster signal"),
    value: typeof item?.value === "number" ? item.value : null,
    hint: cleanText(item?.hint, "No description provided."),
    tone: normalizeTone(item?.tone),
  }));

  const live = mapLiveStatus(liveStatus?.data);
  const workerState = live?.state ?? cleanText(health.worker?.state, "unknown");
  const latestPlayer = live?.latestPlayer ?? cleanText(health.sync?.lastPlayer, "");
  const latestAge = live?.latestAge ?? cleanText(health.sync?.syncedAgo, "");

  return {
    title: "Dashboard",
    subtitle: "Roster coverage, tracker health, and work that needs attention.",
    summary: [
      { key: "members", label: "Members", value: numberOrNull(overview.totalMembers), detail: "Players in the tracker" },
      { key: "active", label: "Active", value: numberOrNull(overview.activeMembers), detail: "Currently active members" },
      { key: "reviews", label: "Open reviews", value: numberOrNull(overview.openAdminCases), detail: "Identity and roster cases" },
      { key: "promotions", label: "Promotions", value: numberOrNull(overview.pendingPromotions), detail: "Candidates awaiting review" },
    ],
    healthRows: [
      {
        label: "API",
        value: formatDisplayLabel(health.api?.state, "Unknown"),
        detail: typeof health.api?.latencyMs === "number" && health.api.latencyMs > 0 ? `${health.api.latencyMs} ms` : "Connected",
        tone: healthTone(health.api?.state),
      },
      {
        label: "Worker",
        value: formatDisplayLabel(workerState, "Unknown"),
        detail: live?.message ?? ([latestPlayer && `Latest player: ${latestPlayer}`, health.worker?.lastHeartbeatAgo && `Heartbeat ${health.worker.lastHeartbeatAgo}`].filter(Boolean).join(" · ") || "No worker detail"),
        tone: healthTone(workerState),
      },
      {
        label: "Latest sync",
        value: latestPlayer || "No completed sync",
        detail: latestAge || "Timestamp unavailable",
        tone: latestPlayer ? "info" : "neutral",
      },
    ],
    posture,
    workItems: normalizeArray(home?.workPreview).map((item) => ({
      id: cleanText(item?.caseId, "review-case"),
      label: cleanText(item?.label, "Review case"),
      risk: cleanText(item?.risk, "unknown"),
      age: cleanText(item?.age, "Age unavailable"),
      tone: normalizeTone(item?.risk),
    })),
    activity: normalizeArray(home?.meaningfulChanges).map((item, index) => ({
      id: item?.id ?? `change-${index}`,
      time: cleanText(item?.time, "Time unavailable"),
      title: cleanText(item?.title, "Tracker event"),
      category: formatDisplayLabel(item?.category, "Activity"),
      tone: normalizeTone(item?.tone),
    })),
    liveStatus: {
      loading: Boolean(liveStatus?.loading && !liveStatus?.data),
      error: cleanText(liveStatus?.error, ""),
      stale: Boolean(liveStatus?.stale),
    },
  };
}

function mapLiveStatus(payload) {
  const components = normalizeArray(payload?.components);
  const worker = components.find((item) => {
    const component = cleanText(item?.component, "").toLowerCase();
    return component && component !== "api" && component !== "latest sync" && component !== "recent event";
  });
  const latest = components.find((item) => cleanText(item?.component, "").toLowerCase() === "latest sync");

  if (!worker && !latest) return null;

  return {
    state: cleanText(worker?.state, "unknown"),
    message: cleanText(worker?.message, ""),
    latestPlayer: cleanText(latest?.currentPlayer, ""),
    latestAge: typeof latest?.ageSeconds === "number" ? formatAge(latest.ageSeconds) : "",
  };
}

function formatAge(seconds) {
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

function numberOrNull(value) {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function normalizeTone(value) {
  const text = cleanText(value, "neutral").toLowerCase();
  if (["success", "warning", "danger", "info", "neutral"].includes(text)) return text;
  if (text === "high") return "danger";
  if (text === "medium") return "warning";
  return "neutral";
}

function healthTone(value) {
  const text = cleanText(value, "").toLowerCase();
  if (["healthy", "online", "idle", "active"].includes(text)) return "success";
  if (["warning", "stale", "syncing", "waiting"].includes(text)) return "warning";
  if (["critical", "offline", "error"].includes(text)) return "danger";
  return statusTone(text);
}
