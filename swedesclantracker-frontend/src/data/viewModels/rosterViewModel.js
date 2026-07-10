import { cleanText, formatDateTime, formatDisplayLabel, statusTone } from "./formatters";

export function mapRosterToRosterViewModel(rows) {
  const safeRows = Array.isArray(rows) ? rows : [];
  const mappedRows = safeRows.map(mapRosterRow);
  const statusOptions = Array.from(new Set(mappedRows.map((row) => row.statusRaw).filter(Boolean)))
    .sort((left, right) => left.localeCompare(right))
    .map((status) => ({
      value: status,
      label: formatDisplayLabel(status),
    }));

  return {
    title: "Clan Members",
    subtitle: "Roster coverage and sync freshness.",
    rows: mappedRows,
    statusOptions,
    summary: {
      total: mappedRows.length,
      stale: mappedRows.filter((row) => row.isSyncStale).length,
      review: mappedRows.filter((row) => row.hasOpenReviewCase).length,
      promotions: mappedRows.filter((row) => row.hasPendingPromotion).length,
      rankMismatch: mappedRows.filter((row) => row.hasRankMismatch).length,
    },
  };
}

function mapRosterRow(row) {
  const flags = buildFlags(row);
  const statusRaw = cleanText(row.status, "UNKNOWN");

  return {
    id: row.id ?? null,
    username: cleanText(row.username, "Unknown"),
    rank: cleanText(row.rank, "Unknown"),
    statusRaw,
    statusLabel: formatDisplayLabel(statusRaw),
    statusTone: statusTone(statusRaw),
    lastSync: formatDateTime(row.lastSync),
    lastSeen: formatDateTime(row.lastSeen),
    isSyncStale: Boolean(row.isSyncStale),
    hasOpenReviewCase: Boolean(row.hasOpenReviewCase),
    hasPendingPromotion: Boolean(row.hasPendingPromotion),
    hasRankMismatch: Boolean(row.hasRankMismatch),
    flags,
  };
}

function buildFlags(row) {
  const flags = [];

  if (row.isSyncStale) {
    flags.push({ key: "stale", label: "stale", tone: "warning" });
  }

  if (row.hasOpenReviewCase) {
    flags.push({ key: "review", label: "review", tone: "warning" });
  }

  if (row.hasPendingPromotion) {
    flags.push({ key: "promotion", label: "promotion", tone: "success" });
  }

  if (row.hasRankMismatch) {
    flags.push({ key: "mismatch", label: "mismatch", tone: "danger" });
  }

  if (!flags.length) {
    flags.push({ key: "clear", label: "clear", tone: "info" });
  }

  return flags;
}
