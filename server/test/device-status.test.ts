import { describe, expect, it } from "vitest";
import { deviceStatus } from "../public/device-status.js";

const now = Date.parse("2026-09-26T12:00:00.000Z");

describe("admin device status dot", () => {
  it("is green when a recent heartbeat says the timer is counting", () => {
    expect(deviceStatus({ last_seen: new Date(now - 30_000).toISOString(), counting: true }, now))
      .toEqual({ dot: "counting", label: "Online — timer is counting" });
  });

  it("is orange when online but not counting", () => {
    expect(deviceStatus({ last_seen: new Date(now - 30_000).toISOString(), counting: false }, now))
      .toEqual({ dot: "paused", label: "Online — timer is not counting" });
  });

  it("keeps older clients orange and explains that their timer state is unknown", () => {
    expect(deviceStatus({ last_seen: new Date(now - 30_000).toISOString() }, now))
      .toEqual({ dot: "paused", label: "Online — timer status unavailable; update the client" });
  });

  it("is gray without a heartbeat or after the five-minute timeout", () => {
    expect(deviceStatus({ last_seen: new Date(now - 5 * 60_000 - 1).toISOString(), counting: true }, now).dot)
      .toBe("offline");
    expect(deviceStatus({ last_seen: null, counting: false }, now).dot).toBe("offline");
  });
});
