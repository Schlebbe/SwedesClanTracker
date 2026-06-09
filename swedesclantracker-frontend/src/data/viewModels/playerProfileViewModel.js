import { cleanText, formatDateParts, formatDisplayLabel, normalizeArray, statusTone } from "./formatters";

export const playerProfileFeatureAvailability = {
  totalXp: {
    label: "Total XP",
    available: false,
    reason: "Requires TotalXp in player snapshots.",
  },
  combatLevel: {
    label: "Combat Level",
    available: false,
    reason: "Requires combat level collection.",
  },
  skillBreakdown: {
    label: "Skill Breakdown",
    available: false,
    reason: "Requires per-skill snapshots.",
  },
  bossKc: {
    label: "Boss KC",
    available: false,
    reason: "Requires boss KC snapshots.",
  },
  recentDrops: {
    label: "Recent Drops",
    available: false,
    reason: "Requires a drops source.",
  },
  rankHistory: {
    label: "Rank History",
    available: false,
    reason: "Requires productionized rank history data.",
  },
  adminNotes: {
    label: "Admin Notes",
    available: false,
    reason: "Requires an admin notes model and save endpoint.",
  },
};

const snapshotFields = [
  { key: "totalLevel", label: "Total Level" },
  { key: "ehb", label: "EHB" },
  { key: "ehp", label: "EHP" },
  { key: "collections", label: "Collection Count" },
  { key: "petCount", label: "Pets" },
];

export function mapPlayerProfileToViewModel(player) {
  if (!player) return null;

  const statusRaw = cleanText(player.status, "UNKNOWN");
  const lastSync = formatDateParts(player.lastSync);
  const lastSeen = formatDateParts(player.lastSeen);
  const openCases = normalizeArray(player.openCases).map(mapOpenCase);
  const recentEvents = normalizeArray(player.recentEvents).map(mapRecentEvent);
  const latestSnapshot = mapLatestSnapshot(player);
  const historyReason = cleanText(player.historyAvailability?.reason, playerProfileFeatureAvailability.rankHistory.reason);

  return {
    id: player.id,
    username: cleanText(player.username, "Unknown player"),
    currentRank: cleanText(player.currentRank, "Unknown"),
    eligibleRank: cleanText(player.eligibleRank, ""),
    statusLabel: formatDisplayLabel(statusRaw),
    statusTone: statusTone(statusRaw),
    lastSync,
    lastSeen,
    isSyncMissing: Boolean(player.isSyncMissing),
    hasPendingPromotion: Boolean(player.hasPendingPromotion),
    hasRankMismatch: Boolean(player.hasRankMismatch),
    openCases,
    recentEvents,
    latestSnapshot,
    summaryCards: [
      {
        key: "current-rank",
        label: "Current Rank",
        value: cleanText(player.currentRank, "Unknown"),
        detail: "Rank currently stored by the tracker",
        tone: "neutral",
        available: true,
      },
      {
        key: "eligible-rank",
        label: "Eligible Rank",
        value: cleanText(player.eligibleRank, ""),
        detail: "Calculated promotion target when available",
        tone: player.eligibleRank ? "success" : "neutral",
        available: Boolean(cleanText(player.eligibleRank, "")),
        unavailableReason: "No eligible rank is exposed for this player.",
      },
      {
        key: "last-seen",
        label: "Last Seen",
        value: lastSeen.short,
        detail: lastSeen.full,
        tone: "neutral",
        available: lastSeen.available,
      },
      {
        key: "last-sync",
        label: "Last Sync",
        value: lastSync.short,
        detail: lastSync.full,
        tone: player.isSyncMissing ? "warning" : "success",
        available: lastSync.available,
        unavailableReason: "No successful sync timestamp is exposed for this player.",
      },
    ],
    futureSections: [
      playerProfileFeatureAvailability.totalXp,
      playerProfileFeatureAvailability.combatLevel,
      playerProfileFeatureAvailability.skillBreakdown,
      playerProfileFeatureAvailability.bossKc,
      playerProfileFeatureAvailability.recentDrops,
      {
        ...playerProfileFeatureAvailability.rankHistory,
        reason: historyReason,
      },
      playerProfileFeatureAvailability.adminNotes,
    ],
  };
}

function mapOpenCase(item, index) {
  const type = cleanText(item?.type, "case").toLowerCase();

  return {
    id: `${type}-${index}`,
    type,
    label: cleanText(item?.label, "Open case"),
    tone: caseTone(type),
  };
}

function mapRecentEvent(item, index) {
  return {
    id: item?.id ?? `event-${index}`,
    title: cleanText(item?.title, "Player event"),
    occurredAt: formatDateParts(item?.occurredAt),
    timeAgo: cleanText(item?.timeAgo, ""),
    tone: "info",
  };
}

function mapLatestSnapshot(player) {
  const source = player.latestSnapshot ?? player.snapshot ?? player.latestSnapshotValues ?? player;
  const rows = snapshotFields
    .map((field) => ({
      key: field.key,
      label: field.label,
      value: source?.[field.key],
    }))
    .filter((row) => row.value !== null && row.value !== undefined && row.value !== "");

  return {
    available: rows.length > 0,
    rows: rows.map((row) => ({
      ...row,
      value: formatSnapshotValue(row.value),
    })),
    reason: "The player profile endpoint does not expose latest snapshot values yet.",
  };
}

function caseTone(type) {
  if (type === "mismatch") return "danger";
  if (type === "promotion") return "success";
  if (type === "review") return "warning";
  return "neutral";
}

function formatSnapshotValue(value) {
  if (typeof value === "number") {
    return Number.isInteger(value) ? value.toLocaleString() : value.toLocaleString(undefined, { maximumFractionDigits: 2 });
  }

  return String(value);
}
