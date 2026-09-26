import { describe, it, expect, afterEach } from "vitest";
import { localDate, recentDates, timeZone } from "../src/clock.js";

const originalZone = process.env.TIME_ZONE;
afterEach(() => {
  if (originalZone === undefined) delete process.env.TIME_ZONE;
  else process.env.TIME_ZONE = originalZone;
});
describe("server calendar", () => {
  it("uses Warsaw's date after local midnight, not UTC", () => {
    process.env.TIME_ZONE = "Europe/Warsaw";
    expect(localDate(new Date("2026-09-26T22:30:00Z"))).toBe("2026-09-27");
    expect(recentDates(new Date("2026-09-26T22:30:00Z"))[1]).toBe("2026-09-26");
  });
  it("handles winter and DST transitions without missing dates", () => {
    process.env.TIME_ZONE = "Europe/Warsaw";
    expect(localDate(new Date("2026-01-01T23:30:00Z"))).toBe("2026-01-02");
    const days = recentDates(new Date("2026-03-30T00:00:00Z"));
    expect(days.slice(0, 3)).toEqual(["2026-03-30", "2026-03-29", "2026-03-28"]);
  });
  it("supports configuration and rejects invalid zones", () => {
    process.env.TIME_ZONE = "America/New_York";
    expect(localDate(new Date("2026-09-26T00:30:00Z"))).toBe("2026-09-25");
    process.env.TIME_ZONE = "invalid/zone";
    expect(timeZone).toThrow();
  });
});
