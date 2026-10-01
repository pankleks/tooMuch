import { describe, expect, it } from "vitest";
import { createWriteLock } from "../src/write-lock.js";

describe("device write locks", () => {
  it("serializes one device while other devices proceed", async () => {
    const lock = createWriteLock();
    let release!: () => void;
    const gate = new Promise<void>(resolve => { release = resolve; });
    const events: string[] = [];
    const first = lock("a", async () => { events.push("first"); await gate; });
    const second = lock("a", async () => { events.push("second"); });
    await lock("b", async () => { events.push("other"); });
    expect(events).toEqual(["first", "other"]);
    release();
    await Promise.all([first, second]);
    expect(events).toEqual(["first", "other", "second"]);
  });

  it("releases the lock after an operation throws", async () => {
    const lock = createWriteLock();
    const failed = lock("a", async () => { throw new Error("write failed"); });
    const next = lock("a", async () => "saved");
    await expect(failed).rejects.toThrow("write failed");
    await expect(next).resolves.toBe("saved");
  });
});
