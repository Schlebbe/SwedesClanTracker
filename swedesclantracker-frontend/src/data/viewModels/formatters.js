export function cleanText(value, fallback = "") {
  if (value === null || value === undefined) return fallback;
  const text = String(value).trim();
  return text.length ? text : fallback;
}

export function normalizeArray(value) {
  return Array.isArray(value) ? value : [];
}

export function formatDisplayLabel(value, fallback = "Unknown") {
  return cleanText(value, fallback)
    .split(/[\s_-]+/)
    .filter(Boolean)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1).toLowerCase())
    .join(" ");
}

export function formatDateTime(value, fallback = "Unknown") {
  const date = parseDate(value);
  return date ? date.toLocaleString("sv-SE") : fallback;
}

export function formatDateParts(value) {
  const date = parseDate(value);

  if (!date) {
    return {
      available: false,
      short: "Unknown",
      full: "Timestamp unavailable",
    };
  }

  return {
    available: true,
    short: date.toLocaleDateString("sv-SE"),
    full: date.toLocaleString("sv-SE"),
  };
}

export function statusTone(value) {
  const text = cleanText(value, "").toUpperCase();
  if (!text) return "neutral";
  if (text.includes("MISSING") || text.includes("MISMATCH")) return "danger";
  if (text.includes("REVIEW") || text.includes("MERGE") || text.includes("PENDING")) return "warning";
  if (text.includes("ACTIVE") || text.includes("SYNCED") || text.includes("CLEAR")) return "success";
  return "info";
}

export function riskTone(value) {
  const text = cleanText(value, "").toLowerCase();
  if (text === "high") return "danger";
  if (text === "medium") return "warning";
  if (text === "low") return "success";
  return "neutral";
}

function parseDate(value) {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}
