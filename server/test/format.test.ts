import { describe, expect, it } from "vitest";
import { formatMinutes, formatQuota, formatTimeAgo, remainingMinutes } from "../public/format.js";

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

  it.each([
    ["limit", "1440m", 23, 1417],
    ["limit", "30m", 30, 0],
    ["limit", "15m", 20, 0],
    ["window", "16:00-20:00", 23, null],
    ["limit", "unknown", 0, null],
  ])("calculates remaining daily-limit minutes for %s quota %s after %i used", (mode, quota, used, expected) => {
    expect(remainingMinutes(mode, quota, used)).toBe(expected);
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
