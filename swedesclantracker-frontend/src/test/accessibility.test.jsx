import { render } from "@testing-library/react";
import axe from "axe-core";
import { describe, expect, it } from "vitest";
import { ConsoleLoginSurface } from "../surfaces/ConsoleLoginSurface";

describe("accessibility foundation", () => {
  it("has no axe violations on the authenticated entry form", async () => {
    const { container } = render(<ConsoleLoginSurface onSubmit={() => {}} submitting={false} error="" returnTo="/" />);
    const results = await axe.run(container);
    expect(results.violations).toEqual([]);
  });
});
