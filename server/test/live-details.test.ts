import { describe, expect, it, vi } from "vitest";
import { updateLiveDetails } from "../public/live-details.js";

function fixture() {
  const nodes = new Map<string, any>();
  const node = (selector: string) => {
    if (!nodes.has(selector)) nodes.set(selector, {
      textContent: "", title: "", style: {}, disabled: false,
      setAttribute: vi.fn(), querySelector: vi.fn(node),
    });
    return nodes.get(selector);
  };
  return { card: { querySelector: vi.fn(node) }, node };
}

describe("live detail refresh", () => {
  it("updates usage and window controls in place without touching the form", () => {
    const { card, node } = fixture();
    updateLiveDetails(card, { today: { mode: "window", quota: "08:00-10:00,17:00-21:00", active_min: 46 }, force_lock: true });
    expect(node(".bar-label").textContent).toBe("5h 14m");
    expect(node(".bar i").style.width).toBe("13%");
    expect(node(".usage-limit strong").textContent).toBe("08:00-10:00,17:00-21:00");
    expect(node('[data-act="daily-bonus"]').disabled).toBe(true);
    expect(node('[data-act="lock"]').title).toBe("Unlock device");
    expect(card.querySelector.mock.calls.flat().join(" ")).not.toMatch(/input|config|focus/);
  });

  it("refreshes limits and re-enables bonus when the schedule changes", () => {
    const { card, node } = fixture();
    updateLiveDetails(card, { today_mode: "window", today_quota: "08:00-10:00", force_lock: false });
    updateLiveDetails(card, { today: { mode: "limit", quota: "120m", active_min: 30 }, force_lock: false });
    expect(node(".bar-label").textContent).toBe("1h 30m");
    expect(node(".bar i").style.width).toBe("25%");
    expect(node(".usage-limit strong").textContent).toBe("2h");
    expect(node('[data-act="daily-bonus"]').disabled).toBe(false);
  });
});
