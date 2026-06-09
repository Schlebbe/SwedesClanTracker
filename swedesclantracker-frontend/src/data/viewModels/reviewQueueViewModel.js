import { cleanText, normalizeArray, riskTone } from "./formatters";

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
  const items = normalizeArray(cases).map(mapListItem);
  const itemsByGroup = items.reduce((groups, item) => {
    const groupId = findReviewGroupId(item);
    const bucket = groups.get(groupId) ?? [];
    bucket.push(item);
    groups.set(groupId, bucket);
    return groups;
  }, new Map());

  const groupedItems = REVIEW_GROUPS.map((group) => ({
    ...group,
    items: itemsByGroup.get(group.id) ?? [],
  }));

  const uncategorizedItems = itemsByGroup.get("other-reviews") ?? [];
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

  const selectedSummary = items.find((item) => item.caseId === selectedCaseId) ?? null;

  return {
    totalCount: items.length,
    groups,
    selectedCase: mapDetailItem(selectedCase, selectedSummary),
  };
}

function mapListItem(item, index) {
  const risk = cleanText(item?.risk, "unknown").toLowerCase();
  const confidence = cleanText(item?.confidence, "");
  const caseId = cleanText(item?.id, "");

  return {
    id: caseId || `case-${index}`,
    caseId,
    canOpenDetail: Boolean(caseId),
    type: cleanText(item?.type, "review"),
    lane: cleanText(item?.lane, "inspect"),
    player: cleanText(item?.player, "Unknown player"),
    title: cleanText(item?.title, "Review case"),
    risk,
    riskTone: riskTone(risk),
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
    riskTone: riskTone(risk),
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

function findReviewGroupId(item) {
  return REVIEW_GROUPS.find((group) => group.matches(item))?.id ?? "other-reviews";
}

function normalizeList(value) {
  return normalizeArray(value).map((item) => cleanText(item, "")).filter(Boolean);
}
