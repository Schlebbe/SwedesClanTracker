const EMPTY = "Unavailable";

export function mapOverviewToViewModel(data) {
  return {
    serverTime: data?.serverTime ?? null,
    health: data?.health ?? null,
    lanes: Array.isArray(data?.openWork) ? data.openWork : [],
    attention: Array.isArray(data?.rosterAttention) ? data.rosterAttention : [],
    recentChanges: Array.isArray(data?.recentChanges) ? data.recentChanges : [],
  };
}

export function mapRosterToViewModel(data) {
  return {
    serverTime: data?.serverTime ?? null,
    page: data?.page ?? 1,
    pageSize: data?.pageSize ?? 50,
    totalCount: data?.totalCount ?? 0,
    totalPages: data?.totalPages ?? 1,
    rows: Array.isArray(data?.items) ? data.items : [],
  };
}

export function mapProfileToViewModel(data) {
  if (!data) return null;
  return {
    ...data,
    latestSnapshot: data.latestSnapshot ?? null,
    openCases: Array.isArray(data.openCases) ? data.openCases : [],
    lifecycleEvents: Array.isArray(data.lifecycleEvents) ? data.lifecycleEvents : [],
    trend: Array.isArray(data.trend) ? data.trend : [],
    supportedTrendRanges: Array.isArray(data.supportedTrendRanges) ? data.supportedTrendRanges : ["30d", "90d", "all"],
  };
}

export function mapReviewsToViewModel(data) {
  return {
    serverTime: data?.serverTime ?? null,
    page: data?.page ?? 1,
    pageSize: data?.pageSize ?? 50,
    totalCount: data?.totalCount ?? 0,
    totalPages: data?.totalPages ?? 1,
    lanes: Array.isArray(data?.lanes) ? data.lanes : [],
    items: Array.isArray(data?.items) ? data.items : [],
  };
}

export function mapReviewDetailToViewModel(data) {
  return data ? {
    ...data,
    case: mapReviewCase(data.case),
    suppressedSignals: Array.isArray(data.suppressedSignals) ? data.suppressedSignals : [],
  } : null;
}

export function mapActivityToViewModel(data) {
  return {
    serverTime: data?.serverTime ?? null,
    totalCount: data?.totalCount ?? 0,
    eventTypes: Array.isArray(data?.eventTypes) ? data.eventTypes : [],
    statuses: Array.isArray(data?.statuses) ? data.statuses : [],
    items: Array.isArray(data?.items) ? data.items : [],
  };
}

export function mapSystemToViewModel(data) {
  return {
    serverTime: data?.serverTime ?? null,
    health: data?.health ?? null,
    checks: Array.isArray(data?.checks) ? data.checks : [],
    configuration: Array.isArray(data?.configuration) ? data.configuration : [],
  };
}

export function mapReviewCase(item) {
  return item ? {
    ...item,
    evidence: Array.isArray(item.evidence) ? item.evidence : [],
    observations: Array.isArray(item.observations) ? item.observations : [],
    warnings: Array.isArray(item.warnings) ? item.warnings : [],
    actions: Array.isArray(item.actions) ? item.actions : [],
    relatedCases: Array.isArray(item.relatedCases) ? item.relatedCases : [],
    supersededCases: Array.isArray(item.supersededCases) ? item.supersededCases : [],
  } : null;
}

export function formatTimestamp(value, options = {}) {
  if (!value) return EMPTY;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return EMPTY;
  return new Intl.DateTimeFormat("sv-SE", {
    dateStyle: "short",
    timeStyle: "short",
    ...options,
  }).format(date);
}

export function formatRelative(value, now = Date.now()) {
  if (!value) return "No timestamp";
  const timestamp = new Date(value).getTime();
  if (Number.isNaN(timestamp)) return "Unknown age";
  const minutes = Math.max(0, Math.floor((now - timestamp) / 60000));
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

export function formatMetric(value, digits = 0) {
  if (value === null || value === undefined) return EMPTY;
  return new Intl.NumberFormat("sv-SE", { maximumFractionDigits: digits }).format(value);
}

export function stateTone(state) {
  switch (String(state ?? "").toLowerCase()) {
    case "healthy":
    case "current":
    case "configured":
    case "success":
    case "done":
      return "success";
    case "warning":
    case "stale":
    case "processing":
    case "open":
    case "unknown":
    case "missing":
      return "warning";
    case "offline":
    case "unavailable":
    case "conflict":
    case "danger":
      return "danger";
    case "info":
      return "info";
    default:
      return "neutral";
  }
}

export function humanize(value) {
  if (!value) return EMPTY;
  return String(value)
    .replaceAll("_", " ")
    .replaceAll("-", " ")
    .toLowerCase()
    .replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());
}
