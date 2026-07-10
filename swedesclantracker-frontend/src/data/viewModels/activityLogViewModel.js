import { cleanText, formatDisplayLabel, normalizeArray } from "./formatters";

const filterLabels = {
  important: "Important",
  promotions: "Promotions",
  roster: "Roster",
  reviews: "Reviews",
  "sync-system": "Sync/System",
  all: "All",
};

const filterGroups = {
  promotions: "Promotion",
  roster: "Roster",
  reviews: "Review",
  "sync-system": "System",
};

export function mapClanLogToActivityLogViewModel(log) {
  const filters = normalizeArray(log?.filters).length
    ? normalizeArray(log.filters)
    : ["important", "promotions", "roster", "reviews", "sync-system", "all"];

  const importantRows = normalizeArray(log?.important).map(mapImportantRow);
  const routineRows = normalizeArray(log?.routine).map(mapRoutineRow);
  const allRows = [...importantRows, ...routineRows];

  return {
    title: "Activity Log",
    subtitle: "Recent lifecycle and tracker events.",
    filters: filters.map((filter) => ({
      id: filter,
      label: filterLabels[filter] ?? formatDisplayLabel(filter),
    })),
    rows: allRows,
    importantRows,
    routineRows,
    hasMemberColumn: allRows.some((row) => row.member),
    hasActionColumn: allRows.some((row) => row.action),
    summary: {
      important: importantRows.length,
      routine: routineRows.length,
      total: allRows.length,
    },
  };
}

export function filterActivityRows(activity, filter) {
  const safeFilter = filter || "important";
  const rows = safeFilter === "all" ? activity.rows : activity.importantRows;

  if (safeFilter === "important" || safeFilter === "all") {
    return rows;
  }

  const group = filterGroups[safeFilter];
  if (!group) {
    return rows.filter((row) => row.filterKey === safeFilter);
  }

  if (safeFilter === "sync-system") {
    return activity.rows.filter((row) => row.group === group);
  }

  return rows.filter((row) => row.group === group);
}

function mapImportantRow(item, index) {
  const group = cleanText(item?.group, "Unknown");
  const title = cleanText(item?.title, "Activity event");
  const detail = cleanText(item?.detail, "No detail provided.");
  const member = cleanText(item?.member ?? item?.player, "");
  const action = mapAction(item);

  return {
    id: cleanText(item?.id, `important-${index}`),
    source: "important",
    group,
    filterKey: group.toLowerCase(),
    title,
    typeLabel: group,
    detail,
    member,
    time: cleanText(item?.time ?? item?.timestamp ?? item?.occurredAt, "unknown"),
    statusLabel: statusLabelForGroup(group),
    tone: toneForGroup(group),
    action,
  };
}

function mapRoutineRow(item, index) {
  return {
    id: `routine-${index}`,
    source: "routine",
    group: "System",
    filterKey: "sync-system",
    title: "Routine tracker maintenance",
    typeLabel: "System",
    detail: cleanText(item, "Routine system event."),
    member: "",
    time: "routine",
    statusLabel: "Routine",
    tone: "info",
    action: null,
  };
}

function mapAction(item) {
  const label = cleanText(item?.actionLabel ?? item?.action, "");
  const target = cleanText(item?.actionTarget ?? item?.actionHref ?? item?.actionCaseId, "");

  if (!label || !target) return null;

  return { label, target };
}

function statusLabelForGroup(group) {
  const normalized = cleanText(group, "").toLowerCase();
  if (normalized === "promotion") return "Promotion";
  if (normalized === "review") return "Review";
  if (normalized === "roster") return "Roster";
  if (normalized === "system") return "System";
  return "General";
}

function toneForGroup(group) {
  const normalized = cleanText(group, "").toLowerCase();
  if (normalized === "promotion") return "success";
  if (normalized === "review") return "warning";
  if (normalized === "roster") return "info";
  if (normalized === "system") return "info";
  return "neutral";
}
