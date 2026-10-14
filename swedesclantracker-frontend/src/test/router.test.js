import { describe, expect, it } from "vitest";
import { readRoute, routeHref } from "../app/router";

describe("operator console routing", () => {
  it("parses direct profile and review detail routes", () => {
    expect(readRoute({ pathname: "/members/42", search: "?range=90d" })).toMatchObject({ name: "profile", playerId: "42" });
    expect(readRoute({ pathname: "/reviews/temple-missing%3A501", search: "" })).toMatchObject({ name: "review-detail", caseId: "temple-missing:501" });
  });

  it("preserves return-to routes in login links", () => {
    expect(routeHref("login", { returnTo: "/reviews?family=promotion" })).toBe("/login?returnTo=%2Freviews%3Ffamily%3Dpromotion");
  });

  it("returns a not-found route for unknown paths", () => {
    expect(readRoute({ pathname: "/unknown", search: "" }).name).toBe("not-found");
  });
});
