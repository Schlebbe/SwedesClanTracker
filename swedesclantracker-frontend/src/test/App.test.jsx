import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import App from "../App";
import { ApiError } from "../data/apiClient";
import { clearConsoleCache } from "../app/useConsoleRequest";

const api = vi.hoisted(() => ({
  fetchSession: vi.fn(),
  fetchConsoleOverview: vi.fn(),
  login: vi.fn(),
  logout: vi.fn(),
}));

vi.mock("../data/appDataApi", async (importOriginal) => ({ ...(await importOriginal()), ...api }));

describe("application shell authentication", () => {
  beforeEach(() => {
    clearConsoleCache();
    window.history.replaceState({}, "", "/reviews?family=promotion");
    api.fetchSession.mockReset();
    api.fetchConsoleOverview.mockReset();
    api.login.mockReset();
    api.logout.mockReset();
    api.fetchConsoleOverview.mockResolvedValue({
      serverTime: "2026-07-13T16:50:00Z",
      health: { overall: "healthy", worker: { state: "healthy", detail: "Heartbeat" }, api: { state: "healthy" }, database: { state: "healthy" }, readiness: { state: "configured", configuredCount: 4, requiredCount: 4 }, latestSync: { state: "healthy", detail: "Thorgrim" } },
      openWork: [],
      rosterAttention: [],
      recentChanges: [],
    });
  });

  it("shows login for an unauthenticated direct route and returns after login", async () => {
    api.fetchSession.mockRejectedValueOnce(new ApiError(401));
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Operator sign-in" })).toBeInTheDocument();
    expect(screen.getByText(/return to the requested route/i)).toBeInTheDocument();
    api.login.mockResolvedValueOnce(null);
    api.fetchSession.mockResolvedValueOnce({ username: "admin", roles: ["Admin"], isAdmin: true });
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "admin" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "secret" } });
    fireEvent.submit(screen.getByRole("button", { name: /sign in/i }).closest("form"));
    await waitFor(() => expect(window.location.pathname).toBe("/reviews"));
    expect(window.location.search).toBe("?family=promotion");
  });

  it("renders active navigation and supports logout", async () => {
    window.history.replaceState({}, "", "/members");
    api.fetchSession.mockResolvedValue({ username: "admin", roles: ["Admin"], isAdmin: true });
    render(<App />);
    expect(await screen.findByRole("link", { name: "Members" })).toHaveAttribute("aria-current", "page");
    fireEvent.click(screen.getByRole("button", { name: /log out/i }));
    await waitFor(() => expect(screen.getByRole("heading", { name: "Operator sign-in" })).toBeInTheDocument());
  });
});
