export const dashboardFeatureAvailability = {
  weeklyXp: {
    available: false,
    reason: "Requires TotalXp history from Core/Worker.",
  },
  bossKc: {
    available: false,
    reason: "Requires boss KC snapshots.",
  },
  collectionLogSync: {
    available: false,
    reason: "Requires collection-log sync accuracy data.",
  },
};

export function mapHomeToDashboardViewModel(home, liveStatus) {
  const overview = home?.overview ?? {};
  const health = home?.health ?? {};
  const live = buildLiveWorkerView(liveStatus?.data);

  const trackedMembers = toNumberOrNull(overview.activeMembers);
  const totalMembers = toNumberOrNull(overview.totalMembers);
  const pendingPromotions = toNumberOrNull(overview.pendingPromotions);
  const openAdminCases = toNumberOrNull(overview.openAdminCases);

  return {
    title: "Operational Dashboard",
    subtitle: "Tracker health, roster posture, recent changes, and waiting admin work.",
    statCards: [
      {
        key: "tracked-members",
        label: "Tracked Members",
        value: formatTrackedMembers(trackedMembers, totalMembers),
        detail: totalMembers !== null ? `${totalMembers.toLocaleString()} total members` : "Total member count unavailable",
        tone: "success",
        available: trackedMembers !== null || totalMembers !== null,
      },
      {
        key: "pending-promotions",
        label: "Pending Promotions",
        value: formatNumber(pendingPromotions),
        detail: "Promotion candidates awaiting review",
        tone: pendingPromotions > 0 ? "warning" : "success",
        available: pendingPromotions !== null,
      },
      {
        key: "open-admin-cases",
        label: "Open Admin Cases",
        value: formatNumber(openAdminCases),
        detail: "Review cases currently open",
        tone: openAdminCases > 0 ? "warning" : "success",
        available: openAdminCases !== null,
      },
    ],
    futureStats: [
      {
        key: "weekly-xp",
        label: "Weekly XP Gained",
        icon: "XP",
        available: dashboardFeatureAvailability.weeklyXp.available,
        unavailableReason: dashboardFeatureAvailability.weeklyXp.reason,
      },
      {
        key: "boss-kc",
        label: "Boss KC Logged",
        icon: "KC",
        available: dashboardFeatureAvailability.bossKc.available,
        unavailableReason: dashboardFeatureAvailability.bossKc.reason,
      },
      {
        key: "collection-log",
        label: "Collection Log Sync",
        icon: "CL",
        available: dashboardFeatureAvailability.collectionLogSync.available,
        unavailableReason: dashboardFeatureAvailability.collectionLogSync.reason,
      },
    ],
    healthCards: [
      {
        key: "overall",
        label: "Tracker Health",
        value: formatStatusLabel(health.overall),
        detail: "Combined API and worker posture",
        tone: healthTone(health.overall),
      },
      {
        key: "api",
        label: "API",
        value: formatStatusLabel(health.api?.state),
        detail: typeof health.api?.latencyMs === "number" && health.api.latencyMs > 0 ? `${health.api.latencyMs}ms latency` : "Latency unavailable",
        tone: health.api?.state === "failed" ? "danger" : "success",
      },
      {
        key: "worker",
        label: "Worker",
        value: live.hasLive ? live.label : formatStatusLabel(health.worker?.state),
        detail: live.hasLive
          ? live.currentTask
          : [health.worker?.currentPlayer, health.worker?.lastHeartbeatAgo].filter(Boolean).join(" | ") || "Worker detail unavailable",
        tone: live.hasLive ? live.tone : workerTone(health.worker?.state),
      },
      {
        key: "latest-sync",
        label: "Latest Sync",
        value: live.hasLive ? live.latestSync : formatLatestSync(health.sync),
        detail: "Most recent completed player sync",
        tone: "neutral",
      },
      {
        key: "latest-event",
        label: "Latest Event",
        value: live.hasLive ? live.latestEvent : "No recent worker event yet",
        detail: "Live worker event stream",
        tone: "info",
      },
    ],
    liveStatus: {
      loading: Boolean(liveStatus?.loading && !liveStatus?.data),
      error: liveStatus?.error ?? "",
      stale: Boolean(liveStatus?.stale),
    },
    postureCards: (Array.isArray(home?.rosterPosture) ? home.rosterPosture : []).map((item) => ({
      key: item.label,
      label: item.label ?? "Unknown",
      value: formatNumber(toNumberOrNull(item.value)),
      detail: item.hint ?? "",
      tone: normalizeTone(item.tone),
    })),
    workItems: (Array.isArray(home?.workPreview) ? home.workPreview : []).map((item) => ({
      key: item.caseId ?? item.label,
      label: item.label ?? "Admin case",
      detail: [item.caseId, item.age ? `age ${item.age}` : ""].filter(Boolean).join(" | "),
      tone: riskTone(item.risk),
      risk: item.risk ?? "unknown",
    })),
    recentChanges: (Array.isArray(home?.meaningfulChanges) ? home.meaningfulChanges : []).map((item) => ({
      key: item.id ?? `${item.title}-${item.time}`,
      event: item.title ?? "Untitled event",
      category: item.category ?? "system",
      tone: normalizeTone(item.tone),
      time: item.time ?? "unknown",
    })),
  };
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
    ? `${latestEvent.state}${latestEvent.currentPlayer ? ` | ${latestEvent.currentPlayer}` : ""}`
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

function formatTrackedMembers(activeMembers, totalMembers) {
  if (activeMembers !== null && totalMembers !== null) {
    return `${activeMembers.toLocaleString()} / ${totalMembers.toLocaleString()}`;
  }

  if (activeMembers !== null) {
    return activeMembers.toLocaleString();
  }

  if (totalMembers !== null) {
    return totalMembers.toLocaleString();
  }

  return "-";
}

function formatLatestSync(sync) {
  if (!sync) {
    return "No sync reported";
  }

  return [sync.lastPlayer, sync.syncedAgo].filter(Boolean).join(" | ") || "No sync reported";
}

function formatNumber(value) {
  return value === null ? "-" : value.toLocaleString();
}

function toNumberOrNull(value) {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
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

function healthTone(value) {
  if (value === "critical") return "danger";
  if (value === "warning") return "warning";
  if (value === "healthy") return "success";
  return "neutral";
}

function workerTone(value) {
  if (value === "offline") return "danger";
  if (value === "stale") return "warning";
  if (value === "online") return "success";
  return "info";
}

function riskTone(value) {
  if (value === "high") return "danger";
  if (value === "medium") return "warning";
  if (value === "low") return "success";
  return "neutral";
}

function normalizeTone(value) {
  if (["success", "warning", "danger", "info", "neutral"].includes(value)) {
    return value;
  }

  return "neutral";
}
