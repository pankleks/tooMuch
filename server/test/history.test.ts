import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { expect, it } from "vitest";
import { formatMinutes, formatQuota } from "../public/format.js";

it("renders extra time only in its day's quota, newest first and safely escaped", () => {
  const source = readFileSync(new URL("../public/admin.js", import.meta.url), "utf8");
  const node = { querySelector: () => node, querySelectorAll: () => [], addEventListener() {} };
  const card = { ...node, innerHTML: "" };
  const buildCard = runInNewContext(
    source.slice(source.indexOf("function buildCard(d)"), source.indexOf('window.addEventListener("hashchange"')) + "\nbuildCard;",
    {
      document: { createElement: () => card }, detailPanels: new Map(), drafts: new Map(),
      ICONS: {}, DAY_NAMES: {}, formatMinutes, formatQuota,
      remainingMinutes: () => null, usagePercent: () => 0,
    },
  );
  const entry = { date: "2026-10-12", created_at: "2026-10-12T16:00:00Z", minutes: 15, total_min: 15, user: "<admin>", ip: "192.168.70.116" };
  const device = {
    device_id: "test", config: { days: Object.fromEntries([1, 2, 3, 4, 5, 6, 7].map(day => [day, { limit_min: 120, windows: [] }])) },
    last7: ["2026-10-13", "2026-10-12"].map(date => ({ date, mode: "limit", quota: "120m", active_min: 0 })),
    daily_bonus_log: [entry, { ...entry, total_min: 30 }],
  };
  buildCard(device);
  const rows = card.innerHTML.match(/<tr>.*?<\/tr>/g)!;
  expect(rows[1]).not.toContain("quota-log");
  const quota = rows[2].match(/<td>.*?<\/td>/g)![2];
  expect(quota.match(/class="quota-log"/g)).toHaveLength(2);
  expect(quota.indexOf("total bonus 30m")).toBeLessThan(quota.indexOf("total bonus 15m"));
  expect(quota).toContain("&lt;admin&gt; (192.168.70.116)");
  expect(quota).toContain(new Date(entry.created_at).toLocaleTimeString());
  expect(quota).not.toContain(new Date(entry.created_at).toLocaleDateString());
  expect(card.innerHTML).not.toContain("<details>");
  expect(device.daily_bonus_log[0].total_min).toBe(15);
  buildCard({ ...device, daily_bonus_log: undefined });
  expect(card.innerHTML).not.toContain("quota-log");
});
