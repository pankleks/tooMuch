import { describe, expect, it, vi } from "vitest";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { loadDevice, registerDevice, saveDevice } from "../src/store.js";

const forced: Buffer[] = [];
vi.mock("node:crypto", async importOriginal => {
  const real = await importOriginal<typeof import("node:crypto")>();
  return { ...real, randomBytes: (size: number) => (size === 4 && forced.shift()) || real.randomBytes(size) };
});

describe("store", () => {
  it("never overwrites an existing device when the collision suffix is taken", async () => {
    const dir = mkdtempSync(join(tmpdir(), "st-"));
    const first = (await registerDevice(dir, "pc")).device;
    const taken = (await registerDevice(dir, "pc")).device;
    const suffix = taken.device_id.slice("pc-".length);
    // Force the next registration to draw the taken suffix once, then a fresh one.
    forced.push(Buffer.from(suffix, "hex"));
    const third = (await registerDevice(dir, "pc")).device;
    expect(forced).toHaveLength(0);
    expect(new Set([first.device_id, taken.device_id, third.device_id]).size).toBe(3);
    expect((await loadDevice(dir, taken.device_id))!.token).toBe(taken.token);
  });

  it("keeps only the last 30 days of the extra-time log", async () => {
    const dir = mkdtempSync(join(tmpdir(), "st-"));
    const { device } = await registerDevice(dir, "log");
    device.daily_bonus_log = Array.from({ length: 40 }, (_, i) => {
      const date = new Date(Date.UTC(2026, 0, 1 + i)).toISOString().slice(0, 10);
      return { created_at: `${date}T10:00:00Z`, date, minutes: 15, total_min: 15, user: "admin", ip: "1.2.3.4" };
    });
    await saveDevice(dir, device);
    const log = (await loadDevice(dir, "log"))!.daily_bonus_log!;
    expect(log).toHaveLength(30);
    expect(log[0].date).toBe("2026-01-11");
  });
});
