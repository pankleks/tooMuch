import { describe, expect, it } from "vitest";
import { formatMinutes, formatQuota, formatTimeAgo } from "../public/format.js";

describe("admin duration formatting", () => {
  it.each([
    [0, "0m"], [1, "1m"], [59, "59m"], [60, "1h"],
    [61, "1h 1m"], [90, "1h 30m"], [1440, "24h"],
  ])("formats %i minutes", (minutes, expected) => {
    expect(formatMinutes(minutes)).toBe(expected);
  });

  it("formats minute quotas but preserves window quotas", () => {
    expect(formatQuota("90m")).toBe("1h 30m");
    expect(formatQuota("16:00-20:00")).toBe("16:00-20:00");
    expect(formatQuota(null)).toBe("?");
    expect(formatMinutes(-1)).toBe("?");
  });

  it("formats last-seen as a relative age", () => {
    const now = Date.parse("2026-09-26T13:25:45.817Z");
    expect(formatTimeAgo("2026-09-26T13:24:46.817Z", now)).toBe("59 seconds ago");
    expect(formatTimeAgo("2026-09-26T13:24:45.817Z", now)).toBe("1 minute ago");
    expect(formatTimeAgo("2026-09-26T13:25:44.817Z", now)).toBe("1 second ago");
    expect(formatTimeAgo("2026-09-26T13:23:45.817Z", now)).toBe("2 minutes ago");
    expect(formatTimeAgo(null, now)).toBe("never");
    expect(formatTimeAgo("invalid", now)).toBe("unknown");
  });
});
