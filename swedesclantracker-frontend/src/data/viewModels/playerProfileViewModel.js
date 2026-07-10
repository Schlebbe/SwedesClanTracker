import { cleanText, formatDateParts, formatDisplayLabel, normalizeArray, statusTone } from "./formatters";

export function mapPlayerProfileToViewModel(player) {
  if (!player) return null;

  const statusRaw = cleanText(player.status, "UNKNOWN");
  return {
    id: player.id,
    username: cleanText(player.username, "Unknown player"),
    currentRank: cleanText(player.currentRank, "Unknown"),
    eligibleRank: cleanText(player.eligibleRank, ""),
    statusLabel: formatDisplayLabel(statusRaw),
    statusTone: statusTone(statusRaw),
    lastSync: formatDateParts(player.lastSync),
    lastSeen: formatDateParts(player.lastSeen),
    openCases: normalizeArray(player.openCases).map((item, index) => ({
      id: `${cleanText(item?.type, "case")}-${index}`,
      type: cleanText(item?.type, "case"),
      label: cleanText(item?.label, "Open case"),
      tone: caseTone(item?.type),
    })),
    recentEvents: normalizeArray(player.recentEvents).map((item, index) => ({
      id: item?.id ?? `event-${index}`,
      title: cleanText(item?.title, "Player event"),
      occurredAt: formatDateParts(item?.occurredAt),
      timeAgo: cleanText(item?.timeAgo, "Age unavailable"),
    })),
    historyAvailability: player.historyAvailability ?? null,
  };
}

function caseTone(type) {
  const normalized = cleanText(type, "").toLowerCase();
  if (normalized === "mismatch") return "danger";
  if (normalized === "promotion") return "success";
  if (normalized === "review") return "warning";
  return "neutral";
}
