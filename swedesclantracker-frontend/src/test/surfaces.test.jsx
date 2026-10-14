import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { clearConsoleCache } from "../app/useConsoleRequest";
import { ConsoleMembersSurface } from "../surfaces/ConsoleMembersSurface";
import { ConsoleProfileSurface } from "../surfaces/ConsoleProfileSurface";
import { ConsoleReviewsQueueSurface, ConsoleReviewDetailSurface } from "../surfaces/ConsoleReviewsSurface";
import { ConsoleActivitySurface } from "../surfaces/ConsoleActivitySurface";
import { ConsoleSystemSurface } from "../surfaces/ConsoleSystemSurface";

const api = vi.hoisted(() => ({
  fetchConsoleRoster: vi.fn(),
  fetchConsoleProfile: vi.fn(),
  fetchConsoleReviews: vi.fn(),
  fetchConsoleReviewDetail: vi.fn(),
  fetchIgnoredPolicies: vi.fn(),
  fetchConsoleActivity: vi.fn(),
  fetchConsoleSystem: vi.fn(),
}));

vi.mock("../data/appDataApi", () => api);

const route = (path, query = "") => ({ name: path, path: `/${path}`, query: new URLSearchParams(query) });

beforeEach(() => {
  clearConsoleCache();
  Object.values(api).forEach((mock) => mock.mockReset());
  api.fetchConsoleRoster.mockResolvedValue({ page: 1, pageSize: 50, totalCount: 51, totalPages: 2, items: [{ playerId: 7, username: "Åskväder med ett mycket långt OSRS-namn", currentRank: "Captain", eligibleRank: "General", status: "ACTIVE", lastSeen: "2026-07-13T16:00:00Z", lastSynced: "2026-07-13T16:00:00Z", freshness: "current", attention: [], latestSnapshot: { timestamp: "2026-07-13T16:00:00Z", totalLevel: 2100, ehb: 881.2, ehp: 1220.4, collections: 812, petCount: 4 } }] });
  api.fetchConsoleProfile.mockResolvedValue({ playerId: 7, username: "Åskväder med ett mycket långt OSRS-namn", currentRank: "Captain", eligibleRank: "General", status: "ACTIVE", freshness: "current", lastSeen: "2026-07-13T16:00:00Z", lastSynced: "2026-07-13T16:00:00Z", latestSnapshot: { timestamp: "2026-07-13T16:00:00Z", totalLevel: 2100, ehb: 881.2, ehp: 1220.4, collections: 812, petCount: 4 }, openCases: [], lifecycleEvents: [], trend: [{ timestamp: "2026-07-01T16:00:00Z", totalLevel: 2090, ehb: 870, ehp: 1200, collections: 800, petCount: 4 }], supportedTrendRanges: ["30d", "90d", "all"] });
  api.fetchConsoleReviews.mockResolvedValue({ page: 1, pageSize: 50, totalCount: 1, totalPages: 1, lanes: [{ family: "promotion", label: "Promotions", count: 1, tone: "warning" }], items: [{ caseId: "promotion:17", family: "promotion", state: "open", subject: "Thorgrim", summary: "Promotion candidate is still pending.", status: "OPEN", openedAt: "2026-07-13T16:00:00Z", evidence: [{ label: "Eligible rank", value: "General", state: "observed" }], actions: [{ actionId: "approve", label: "Approve promotion", direction: "safe", interactive: false, disabledReason: "Gate 3" }], observations: [], warnings: [], relatedCases: [], supersededCases: [] }] });
  api.fetchIgnoredPolicies.mockResolvedValue({ items: [] });
  api.fetchConsoleReviewDetail.mockResolvedValue({ gateState: "read_only_gate_3", canExecute: false, suppressedSignals: [], case: { caseId: "promotion:17", family: "promotion", state: "open", subject: "Thorgrim", summary: "Promotion candidate is still pending.", status: "OPEN", openedAt: "2026-07-13T16:00:00Z", evidence: [{ label: "Eligible rank", value: "General", state: "observed" }], actions: [{ actionId: "approve", label: "Approve promotion", direction: "safe", interactive: false, disabledReason: "Gate 3" }], observations: ["Promotion candidate recorded."], warnings: ["No mutation in Gate 3."], relatedCases: [], supersededCases: [] } });
  api.fetchConsoleActivity.mockResolvedValue({ totalCount: 1, eventTypes: ["PROMOTION_CANDIDATE_CREATED"], statuses: ["DONE"], items: [{ eventId: "9", eventType: "PROMOTION_CANDIDATE_CREATED", label: "Promotion Candidate Created", status: "DONE", occurredAt: "2026-07-13T16:00:00Z", playerId: 7, playerName: "Thorgrim", source: "sync", evidence: [{ label: "Observation", value: "Candidate recorded." }] }] });
  api.fetchConsoleSystem.mockResolvedValue({ checks: [{ component: "API", state: "healthy", detail: "Online", observedAt: "2026-07-13T16:00:00Z" }], configuration: [{ key: "TempleOsrs:ApiKey", state: "configured", detail: "Value hidden" }] });
});

describe("read-only console surfaces", () => {
  it("supports roster pagination and preserves long Swedish names", async () => {
    const onNavigate = vi.fn();
    render(<ConsoleMembersSurface route={route("members")} onNavigate={onNavigate} />);
    expect((await screen.findAllByText("Åskväder med ett mycket långt OSRS-namn")).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "Next page" }));
    expect(onNavigate).toHaveBeenCalledWith("/members?page=2");
  });

  it("switches profile trend ranges through the URL", async () => {
    const onNavigate = vi.fn();
    render(<ConsoleProfileSurface route={{ name: "profile", path: "/members/7", playerId: "7", query: new URLSearchParams("range=90d") }} onNavigate={onNavigate} />);
    expect((await screen.findAllByRole("heading", { name: "Åskväder med ett mycket långt OSRS-namn" })).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("tab", { name: "30d" }));
    expect(onNavigate).toHaveBeenCalledWith("/members/7?range=30d");
  });

  it("renders typed read-only cases and exact disabled decision direction", async () => {
    const onNavigate = vi.fn();
    render(<ConsoleReviewsQueueSurface route={route("reviews")} onNavigate={onNavigate} />);
    expect(await screen.findByText("Thorgrim")).toBeInTheDocument();
    expect(await screen.findByText("Promotion candidate is still pending.")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Thorgrim"));
    expect(onNavigate).toHaveBeenCalledWith("/reviews/promotion%3A17");
  });

  it("renders case detail without executable controls", async () => {
    render(<ConsoleReviewDetailSurface route={{ name: "review-detail", path: "/reviews/promotion:17", caseId: "promotion:17", query: new URLSearchParams() }} onNavigate={vi.fn()} />);
    expect(await screen.findByText("Read-only foundation. Review execution and external changes are not enabled.")).toBeInTheDocument();
    expect(screen.getByText("Approve promotion")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Approve promotion" })).not.toBeInTheDocument();
  });

  it("renders activity filters and system readiness state", async () => {
    const { rerender } = render(<ConsoleActivitySurface route={route("activity")} onNavigate={vi.fn()} />);
    expect(await screen.findByText("Candidate recorded.")).toBeInTheDocument();
    rerender(<ConsoleSystemSurface />);
    expect(await screen.findByText("TempleOsrs:ApiKey")).toBeInTheDocument();
    expect(screen.getAllByText("Configured").length).toBeGreaterThan(0);
  });
});
