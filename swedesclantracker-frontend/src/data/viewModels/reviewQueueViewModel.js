const REVIEW_GROUPS = [
  {
    id: "rsn-changes",
    label: "Possible RSN Changes",
    description: "Rename and merge suggestions that need identity review.",
    matches: (item) => includesAny(item, ["merge", "rename", "rsn", "previous player"]),
  },
  {
    id: "missing-members",
    label: "Missing Members",
    description: "Missing or newly detected members that need validation.",
    matches: (item) => includesAny(item, ["missing", "new review", "add/remove", "pending review"]),
  },
  {
    id: "rank-reviews",
    label: "Rank Reviews",
    description: "Pending promotions and rank-related officer decisions.",
    matches: (item) => includesAny(item, ["promotion", "rank"]),
  },
];

export function mapAdminQueueToReviewQueueViewModel(cases = [], selectedCase = null, selectedCaseId = "") {
  const items = Array.isArray(cases) ? cases.map(mapListItem) : [];
  const groupedItems = REVIEW_GROUPS.map((group) => ({
    ...group,
    items: items.filter(group.matches),
  }));

  const knownItemIds = new Set(groupedItems.flatMap((group) => group.items.map((item) => item.id)));
  const uncategorizedItems = items.filter((item) => !knownItemIds.has(item.id));
  const groups = uncategorizedItems.length
    ? [
        ...groupedItems,
        {
          id: "other-reviews",
          label: "Other Reviews",
          description: "Cases exposed by the admin queue that do not match the MVP buckets.",
          items: uncategorizedItems,
        },
      ]
    : groupedItems;

  const selectedSummary = items.find((item) => item.id === selectedCaseId) ?? null;

  return {
    totalCount: items.length,
    groups,
    selectedCase: mapDetailItem(selectedCase, selectedSummary),
  };
}

function mapListItem(item) {
  const risk = cleanText(item?.risk, "unknown").toLowerCase();
  const confidence = cleanText(item?.confidence, "");

  return {
    id: cleanText(item?.id, ""),
    type: cleanText(item?.type, "review"),
    lane: cleanText(item?.lane, "inspect"),
    player: cleanText(item?.player, "Unknown player"),
    title: cleanText(item?.title, "Review case"),
    risk,
    riskTone: mapRiskTone(risk),
    confidenceLabel: confidence,
    age: cleanText(item?.age, "unknown age"),
    recommendedAction: cleanText(item?.recommendedAction, ""),
    searchText: [
      item?.id,
      item?.type,
      item?.lane,
      item?.player,
      item?.title,
      item?.risk,
      item?.confidence,
      item?.recommendedAction,
    ]
      .filter(Boolean)
      .join(" ")
      .toLowerCase(),
  };
}

function mapDetailItem(detail, summary) {
  if (!detail && !summary) return null;

  const source = detail ?? summary;
  const risk = cleanText(source?.risk, summary?.risk ?? "unknown").toLowerCase();
  const evidence = normalizeList(source?.evidence);
  const alternatives = normalizeList(source?.alternatives);

  return {
    id: cleanText(source?.id, summary?.id ?? ""),
    type: cleanText(source?.type, summary?.type ?? "review"),
    player: cleanText(source?.player, summary?.player ?? "Unknown player"),
    title: cleanText(source?.title, summary?.title ?? "Review case"),
    risk,
    riskTone: mapRiskTone(risk),
    confidenceLabel: cleanText(source?.confidence, summary?.confidenceLabel ?? ""),
    age: cleanText(source?.age, summary?.age ?? "unknown age"),
    recommendedAction: cleanText(source?.recommendedAction, summary?.recommendedAction ?? ""),
    evidence,
    alternatives,
    dangerousNote: cleanText(source?.dangerous ?? source?.danger, ""),
  };
}

function includesAny(item, terms) {
  return terms.some((term) => item.searchText.includes(term));
}

function cleanText(value, fallback = "") {
  if (value === null || value === undefined) return fallback;
  const text = String(value).trim();
  return text.length ? text : fallback;
}

function normalizeList(value) {
  return Array.isArray(value)
    ? value.map((item) => cleanText(item, "")).filter(Boolean)
    : [];
}

function mapRiskTone(risk) {
  if (risk === "high") return "danger";
  if (risk === "medium") return "warning";
  if (risk === "low") return "success";
  return "neutral";
}
