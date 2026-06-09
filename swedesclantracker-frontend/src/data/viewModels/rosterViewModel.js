export function mapRosterToRosterViewModel(rows) {
  const safeRows = Array.isArray(rows) ? rows : [];
  const mappedRows = safeRows.map(mapRosterRow);
  const statusOptions = Array.from(new Set(mappedRows.map((row) => row.statusRaw).filter(Boolean)))
    .sort((left, right) => left.localeCompare(right))
    .map((status) => ({
      value: status,
      label: formatStatusLabel(status),
    }));

  return {
    title: "Clan Members",
    subtitle: "Roster scanning, sync freshness, review flags, and profile entry points.",
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

  return {
    id: row.id,
    username: row.username ?? "Unknown",
    rank: row.rank ?? "Unknown",
    statusRaw: row.status ?? "UNKNOWN",
    statusLabel: formatStatusLabel(row.status),
    statusTone: statusTone(row.status),
    lastSync: formatDate(row.lastSync),
    lastSeen: formatDate(row.lastSeen),
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

function formatDate(value) {
  if (!value) {
    return "Unknown";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "Unknown";
  }

  return date.toLocaleString("sv-SE");
}

function statusTone(value) {
  if (typeof value !== "string") {
    return "neutral";
  }

  if (value.includes("MISSING") || value.includes("MISMATCH")) {
    return "danger";
  }

  if (value.includes("REVIEW") || value.includes("MERGE") || value.includes("PENDING")) {
    return "warning";
  }

  if (value.includes("ACTIVE")) {
    return "success";
  }

  return "info";
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
