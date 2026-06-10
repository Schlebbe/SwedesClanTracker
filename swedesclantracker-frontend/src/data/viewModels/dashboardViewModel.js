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
  rosterExport: {
    available: false,
    reason: "Requires a roster export endpoint.",
  },
  rosterUpdate: {
    available: false,
    reason: "Requires a safe app-facing sync trigger.",
  },
  adminTools: {
    available: false,
    reason: "Visual target placeholder only; no mutation endpoint is wired.",
  },
};

export const dashboardVisualPlaceholders = {
  unsupportedKpis: {
    weeklyXp: {
      key: "weekly-xp",
      label: "Weekly XP Gained",
      value: "184.2M",
      detail: "Total XP",
      trend: "+32.6M vs last week",
      icon: "stats",
      tone: "success",
      source: "placeholder",
      unavailableReason: dashboardFeatureAvailability.weeklyXp.reason,
    },
    bossKc: {
      key: "boss-kc",
      label: "Boss KC Logged",
      value: "3,421",
      detail: "This week",
      trend: "+512 vs last week",
      icon: "rank",
      tone: "success",
      source: "placeholder",
      unavailableReason: dashboardFeatureAvailability.bossKc.reason,
    },
    collectionLog: {
      key: "collection-log",
      label: "Collection Log Sync",
      value: "118",
      detail: "Items synced",
      trend: "99.1% accuracy",
      icon: "scroll",
      tone: "success",
      source: "placeholder",
      unavailableReason: dashboardFeatureAvailability.collectionLogSync.reason,
    },
  },
  adminTasks: [
    {
      key: "possible-rsn-changes",
      label: "Possible RSN Changes",
      count: 5,
      detail: "Members with names that may have changed.",
      icon: "name-change",
      tone: "danger",
      risk: "High",
      source: "placeholder",
    },
    {
      key: "stale-members",
      label: "Stale Members",
      count: 12,
      detail: "Members inactive for 30+ days.",
      icon: "member-alert",
      tone: "warning",
      risk: "Medium",
      source: "placeholder",
    },
    {
      key: "rank-reviews",
      label: "Rank Reviews",
      count: 8,
      detail: "Promotions or demotions awaiting approval.",
      icon: "shield",
      tone: "warning",
      risk: "Medium",
      source: "placeholder",
    },
  ],
  quickTools: [
    { key: "add-member", label: "Add Member", icon: "add-member", source: "placeholder" },
    { key: "run-audit", label: "Run Audit", icon: "checklist", source: "placeholder" },
    { key: "sync-hiscores", label: "Sync HiScores", icon: "sync", source: "placeholder" },
    { key: "clear-cache", label: "Clear Temp Cache", icon: "clean", source: "placeholder" },
  ],
};

export function mapHomeToDashboardViewModel(home, liveStatus) {
  const overview = home?.overview ?? {};
  const health = home?.health ?? {};
  const live = buildLiveWorkerView(liveStatus?.data);

  const trackedMembers = toNumberOrNull(overview.activeMembers);
  const totalMembers = toNumberOrNull(overview.totalMembers);
  const pendingPromotions = toNumberOrNull(overview.pendingPromotions);
  const staleSyncCount = findPostureValue(home?.rosterPosture, "stale");
  const missingReviewCount = findPostureValue(home?.rosterPosture, "missing");
  const mergeReviewCount = findPostureValue(home?.rosterPosture, "merge");
  const rankMismatchCount = findPostureValue(home?.rosterPosture, "rank");

  const trackerStatus = buildTrackerStatus(health, live);
  const realTasks = buildAdminTasks({
    workItems: home?.workPreview,
    pendingPromotions,
    staleSyncCount,
    missingReviewCount,
    mergeReviewCount,
    rankMismatchCount,
  });

  return {
    title: "Operational Dashboard",
    subtitle: "Real-time overview of Swedes clan activity, hiscores tracking, and administrative status.",
    trackerStatus,
    kpis: [
      {
        key: "tracked-members",
        label: "Tracked Members",
        value: formatTrackedMembers(trackedMembers, totalMembers),
        detail: totalMembers !== null ? `${formatTrackedPercent(trackedMembers, totalMembers)} tracked` : "Total member count unavailable",
        trend: formatMemberTrend(staleSyncCount),
        icon: "members",
        tone: "success",
        source: "api",
        available: trackedMembers !== null || totalMembers !== null,
      },
      dashboardVisualPlaceholders.unsupportedKpis.weeklyXp,
      dashboardVisualPlaceholders.unsupportedKpis.bossKc,
      dashboardVisualPlaceholders.unsupportedKpis.collectionLog,
    ],
    adminTasks: mergeTaskTargets(realTasks),
    quickTools: dashboardVisualPlaceholders.quickTools.map((item) => ({
      ...item,
      disabled: true,
      reason: dashboardFeatureAvailability.adminTools.reason,
    })),
    activityRows: buildActivityRows(home?.meaningfulChanges),
    footerItems: [
      "SwedesClanTracker",
      "Unofficial OSRS Clan Tracker",
      "Existing API data with marked visual placeholders",
    ],
    liveStatus: {
      loading: Boolean(liveStatus?.loading && !liveStatus?.data),
      error: liveStatus?.error ?? "",
      stale: Boolean(liveStatus?.stale),
    },
    healthCards: [
      {
        key: "overall",
        label: "Tracker Health",
        value: trackerStatus.label,
        detail: trackerStatus.detail,
        tone: trackerStatus.tone,
        source: trackerStatus.source,
      },
      {
        key: "latest-sync",
        label: "Latest Snapshot",
        value: trackerStatus.snapshot,
        detail: "Most recent completed player sync",
        tone: "neutral",
        source: trackerStatus.source,
      },
    ],
    statCards: [],
    futureStats: Object.values(dashboardVisualPlaceholders.unsupportedKpis),
    postureCards: (Array.isArray(home?.rosterPosture) ? home.rosterPosture : []).map((item) => ({
      key: item.label,
      label: item.label ?? "Unknown",
      value: formatNumber(toNumberOrNull(item.value)),
      detail: item.hint ?? "",
      tone: normalizeTone(item.tone),
      source: "api",
    })),
    workItems: realTasks,
    recentChanges: buildActivityRows(home?.meaningfulChanges),
  };
}

function buildTrackerStatus(health, live) {
  if (live.hasLive) {
    return {
      label: live.label,
      detail: live.currentTask,
      snapshot: live.latestSync,
      tone: live.tone,
      source: "api",
    };
  }

  const overall = formatStatusLabel(health?.overall);
  return {
    label: `Tracker ${overall}`,
    detail: health?.worker?.lastHeartbeatAgo ? `Worker heartbeat ${health.worker.lastHeartbeatAgo}` : "Worker detail unavailable",
    snapshot: formatLatestSync(health?.sync),
    tone: healthTone(health?.overall),
    source: "api",
  };
}

function buildAdminTasks({ workItems, pendingPromotions, staleSyncCount, missingReviewCount, mergeReviewCount, rankMismatchCount }) {
  const tasks = [];
  const reviewTotal = sumKnown([missingReviewCount, mergeReviewCount]);
  if (reviewTotal !== null) {
    tasks.push({
      key: "possible-rsn-changes",
      label: "Possible RSN Changes",
      count: reviewTotal,
      detail: "Members with names or membership state needing review.",
      icon: "name-change",
      tone: reviewTotal > 0 ? "danger" : "success",
      risk: reviewTotal > 0 ? "Open" : "Clear",
      source: "api",
    });
  }

  if (staleSyncCount !== null) {
    tasks.push({
      key: "stale-members",
      label: "Stale Members",
      count: staleSyncCount,
      detail: "Members with sync data older than the current freshness window.",
      icon: "member-alert",
      tone: staleSyncCount > 0 ? "warning" : "success",
      risk: staleSyncCount > 0 ? "Open" : "Clear",
      source: "api",
    });
  }

  const rankReviewTotal = sumKnown([pendingPromotions, rankMismatchCount]);
  if (rankReviewTotal !== null) {
    tasks.push({
      key: "rank-reviews",
      label: "Rank Reviews",
      count: rankReviewTotal,
      detail: "Promotions or rank mismatches awaiting approval.",
      icon: "shield",
      tone: rankReviewTotal > 0 ? "warning" : "success",
      risk: rankReviewTotal > 0 ? "Open" : "Clear",
      source: "api",
    });
  }

  if (!tasks.length && Array.isArray(workItems)) {
    return workItems.slice(0, 3).map((item, index) => ({
      key: item.caseId ?? `case-${index}`,
      label: item.label ?? "Admin Case",
      count: index + 1,
      detail: [item.caseId, item.age ? `age ${item.age}` : ""].filter(Boolean).join(" | ") || "Review case available.",
      icon: "review",
      tone: riskTone(item.risk),
      risk: item.risk ?? "unknown",
      source: "api",
    }));
  }

  return tasks;
}

function mergeTaskTargets(realTasks) {
  return dashboardVisualPlaceholders.adminTasks.map((placeholder) => {
    const real = realTasks.find((item) => item.key === placeholder.key);
    if (!real) return placeholder;
    return {
      ...placeholder,
      ...real,
      label: placeholder.label,
      icon: placeholder.icon,
      source: "api",
    };
  });
}

function buildActivityRows(changes) {
  return (Array.isArray(changes) ? changes : []).slice(0, 8).map((item, index) => {
    const tone = normalizeTone(item.tone);
    return {
      key: item.id ?? `activity-${index}`,
      time: item.time ?? "",
      event: item.title ?? "",
      member: item.member ?? item.player ?? "",
      detail: item.detail ?? "",
      status: item.category ?? "",
      tone,
      action: item.action ?? "",
      icon: iconForActivity(item.title, item.category),
      source: "api",
    };
  });
}

function iconForActivity(event, category) {
  const text = `${event ?? ""} ${category ?? ""}`.toLowerCase();
  if (text.includes("promotion")) return "promotion";
  if (text.includes("name") || text.includes("merge")) return "scroll";
  if (text.includes("sync")) return "sync";
  if (text.includes("new") || text.includes("recruit")) return "add-member";
  if (text.includes("rank")) return "rank";
  return "activity";
}

function buildLiveWorkerView(payload) {
  if (!payload?.components?.length) {
    return {
      hasLive: false,
      tone: "warning",
      label: "HiScores Sync Pending",
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

  let label = "HiScores Sync: Stable";
  let tone = "success";
  if (!worker) {
    label = "HiScores Sync Pending";
    tone = "warning";
  } else if (isOffline) {
    label = "HiScores Sync: Offline";
    tone = "danger";
  } else if (isStale) {
    label = "HiScores Sync: Stale";
    tone = "warning";
  } else if (isRateLimited) {
    label = "HiScores Sync: Waiting";
    tone = "warning";
  } else if (workerPlayer) {
    label = `HiScores Sync: ${workerPlayer}`;
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
      ? `Last completed sync also ${workerPlayer}`
      : `Still on ${workerPlayer}`;
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

function formatTrackedPercent(activeMembers, totalMembers) {
  if (activeMembers === null || totalMembers === null || totalMembers <= 0) {
    return "Tracking percentage unavailable";
  }

  return `${((activeMembers / totalMembers) * 100).toFixed(1)}%`;
}

function formatMemberTrend(staleSyncCount) {
  if (staleSyncCount === null) {
    return "Roster freshness unavailable";
  }

  if (staleSyncCount === 0) {
    return "All tracked members fresh";
  }

  return `${staleSyncCount.toLocaleString()} need sync review`;
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

function findPostureValue(items, keyword) {
  if (!Array.isArray(items)) return null;
  const found = items.find((item) => String(item?.label ?? "").toLowerCase().includes(keyword));
  return toNumberOrNull(found?.value);
}

function sumKnown(values) {
  const known = values.filter((value) => value !== null);
  if (!known.length) return null;
  return known.reduce((total, value) => total + value, 0);
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
