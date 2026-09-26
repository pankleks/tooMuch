import { describe, it, expect, beforeEach, vi } from "vitest";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { buildApp } from "../src/index.js";
import { localDate } from "../src/clock.js";

let dir: string;
process.env.VITEST = "true";
process.env.ADMIN_PASSWORD = "test-admin";

describe("api", () => {
  let app: Awaited<ReturnType<typeof buildApp>>;

  beforeEach(async () => {
    dir = mkdtempSync(join(tmpdir(), "st-"));
    process.env.DATA_DIR = dir;
    process.env.DIST_DIR = join(dir, "dist");
    app?.close?.().catch(() => {});
    app = await buildApp();
  });

  it("serves the responsive admin tabs and installable PWA shell", async () => {
    const page = await app.inject({ method: "GET", url: "/admin" });
    expect(page.statusCode).toBe(200);
    expect(page.body).toContain("id=\"list\"");
    expect(page.body).toContain("manifest.webmanifest");
    expect(page.body).toContain("src=\"/admin-static/icons/icon-192.png\"");
    expect(page.body).toContain('aria-label="Refresh devices"');
    expect(page.body).toContain(".tabs{flex-wrap:wrap;overflow:visible");

    const adminScript = await app.inject({ method: "GET", url: "/admin-static/admin.js" });
    expect(adminScript.statusCode).toBe(200);
    expect(adminScript.body).toContain("History");
    expect(adminScript.body).toContain("Config");
    expect(adminScript.body).toContain('aria-label="${lockLabel}"');
    expect(adminScript.body).toContain('aria-label="Send message to child"');
    expect(adminScript.body).toContain('aria-label="Delete device"');

    const manifest = await app.inject({ method: "GET", url: "/admin-static/manifest.webmanifest" });
    expect(manifest.statusCode).toBe(200);
    expect(manifest.headers["content-type"]).toContain("application/manifest+json");
    expect(manifest.json().display).toBe("standalone");
    expect(manifest.json().icons.some((icon: { purpose?: string }) => icon.purpose === "maskable")).toBe(true);
    for (const iconPath of ["icon-192.png", "icon-512.png", "icon-512-maskable.png", "apple-touch-icon.png"]) {
      const icon = await app.inject({ method: "GET", url: `/admin-static/icons/${iconPath}` });
      expect(icon.statusCode).toBe(200);
    }

    const worker = await app.inject({ method: "GET", url: "/service-worker.js" });
    expect(worker.statusCode).toBe(200);
    expect(worker.headers["service-worker-allowed"]).toBe("/");
    expect(worker.body).toContain("url.pathname.startsWith(\"/api/\")");
  });

  it("register -> config 304/200 -> heartbeat -> overview", async () => {
    const reg = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "DESKTOP-JAS" } });
    expect(reg.statusCode).toBe(200);
    const { device_id, token } = reg.json();
    expect(device_id).toBe("desktop-jas");
    // collision creates suffixed id, does not overwrite
    const reg2 = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "DESKTOP-JAS" } });
    expect(reg2.statusCode).toBe(200);
    expect(reg2.json().device_id).not.toBe(device_id);

    const h = { "x-device-token": token };
    const c1 = await app.inject({ method: "GET", url: `/api/config/${device_id}`, headers: h });
    expect(c1.statusCode).toBe(200);
    const cfg = c1.json();
    expect(cfg.version).toBe(1);
    expect(cfg.days["1"].limit_min).toBe(1440);

    const c304 = await app.inject({ method: "GET", url: `/api/config/${device_id}?v=1&tz=Europe%2FWarsaw`, headers: h });
    expect(c304.statusCode).toBe(304);

    // heartbeat "today" must match the server clock (overview keys by current date)
    const todayStr = localDate();
    const hb = await app.inject({
      method: "POST", url: "/api/heartbeat", headers: h,
      payload: { device_id, date: todayStr, active_min: 45, locked: false, counting: true },
    });
    expect(hb.statusCode).toBe(200);

    // admin overview (BasicAuth admin/test-admin)
    const cred = Buffer.from("admin:test-admin").toString("base64");
    const ov = await app.inject({ method: "GET", url: "/api/admin/overview", headers: { authorization: `Basic ${cred}` } });
    expect(ov.statusCode).toBe(200);
    const body = ov.json();
    expect(body.devices.length).toBe(2);
    const me = body.devices.find((d: { device_id: string }) => d.device_id === device_id);
    expect(me.today.active_min).toBe(45);
    expect(me.counting).toBe(true);
    expect(me.last7.length).toBe(7);
  });

  it("reports the latest counting state and clears it for legacy heartbeats", async () => {
    const reg = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "timer-status" } });
    const { device_id, token } = reg.json();
    const headers = { "x-device-token": token };
    const auth = { authorization: `Basic ${Buffer.from("admin:test-admin").toString("base64")}` };
    const send = (counting?: boolean) => app.inject({
      method: "POST", url: "/api/heartbeat", headers,
      payload: { device_id, date: localDate(), active_min: 1, locked: false, ...(counting === undefined ? {} : { counting }) },
    });
    const overviewCounting = async () => (await app.inject({ method: "GET", url: "/api/admin/overview", headers: auth })).json().devices[0].counting;

    expect((await send(true)).statusCode).toBe(200);
    expect(await overviewCounting()).toBe(true);
    expect((await send(false)).statusCode).toBe(200);
    expect(await overviewCounting()).toBe(false);
    expect((await send()).statusCode).toBe(200);
    expect(await overviewCounting()).toBe(null);
  });

  it("admin can set per-day limits + windows override, validation enforced", async () => {
    const reg = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "pc-test" } });
    const { device_id } = reg.json();
    const cred = Buffer.from("admin:test-admin").toString("base64");
    const auth = { authorization: `Basic ${cred}` };

    // set 1-5 ->120, 6-7 ->300
    const days: Record<string, unknown> = {};
    for (let i = 1; i <= 7; i++) days[String(i)] = { limit_min: i <= 5 ? 120 : 300, windows: [] };
    const put = await app.inject({
      method: "PUT", url: `/api/admin/devices/${device_id}`, headers: { ...auth, "content-type": "application/json" },
      payload: { days },
    });
    expect(put.statusCode).toBe(200);
    expect(put.json().version).toBe(2);

    // overnight rejected
    const bad: Record<string, unknown> = JSON.parse(JSON.stringify(days));
    bad["3"] = { limit_min: 120, windows: [{ from: "22:00", to: "01:00" }] };
    const putBad = await app.inject({
      method: "PUT", url: `/api/admin/devices/${device_id}`, headers: { ...auth, "content-type": "application/json" },
      payload: { days: bad },
    });
    expect(putBad.statusCode).toBe(400);

    // force_lock
    const lock = await app.inject({
      method: "PUT", url: `/api/admin/devices/${device_id}`, headers: { ...auth, "content-type": "application/json" },
      payload: { force_lock: true },
    });
    expect(lock.statusCode).toBe(200);
  });

  it("parallel heartbeats cannot undo a saved limit or recreate a deleted device", async () => {
    const reg = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "parallel" } });
    const { device_id, token } = reg.json();
    const auth = { authorization: `Basic ${Buffer.from("admin:test-admin").toString("base64")}` };
    const days = Object.fromEntries([1,2,3,4,5,6,7].map(i => [String(i), { limit_min: 90, windows: [] }]));
    const requests = [app.inject({ method: "PUT", url: `/api/admin/devices/${device_id}`, headers: auth, payload: { days } }),
      ...Array.from({ length: 15 }, (_, i) => app.inject({
        method: "POST", url: "/api/heartbeat", headers: { "x-device-token": token },
        payload: { device_id, date: localDate(), active_min: i, locked: true },
      }))];
    const responses = await Promise.all(requests);
    expect(responses.every(r => r.statusCode === 200)).toBe(true);
    const ov = await app.inject({ method: "GET", url: "/api/admin/overview", headers: auth });
    expect(ov.json().devices[0].config.days["1"].limit_min).toBe(90);
    expect(ov.json().devices[0].today.active_min).toBe(14);
    expect(ov.json().devices[0].locked).toBe(true);
    await Promise.all([
      app.inject({ method: "DELETE", url: `/api/admin/devices/${device_id}`, headers: auth }),
      app.inject({ method: "POST", url: "/api/heartbeat", headers: { "x-device-token": token },
        payload: { device_id, date: localDate(), active_min: 20, locked: false } }),
    ]);
    const after = await app.inject({ method: "GET", url: "/api/admin/overview", headers: auth });
    expect(after.json().devices).toEqual([]);
  });

  it("timezone changes invalidate the client's cached configuration", async () => {
    const reg = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "timezone" } });
    const { device_id, token } = reg.json();
    const response = await app.inject({ method: "GET", url: `/api/config/${device_id}?v=1&tz=UTC`, headers: { "x-device-token": token } });
    expect(response.statusCode).toBe(200);
    expect(response.json().time_zone).toBe("Europe/Warsaw");
  });

  it("parent messages require auth, stay queued, expire and preserve confirmation", async () => {
    const reg = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "messages" } });
    const { device_id, token } = reg.json();
    const admin = { authorization: `Basic ${Buffer.from("admin:test-admin").toString("base64")}` };
    const child = { "x-device-token": token };
    const send = `/api/admin/devices/${device_id}/messages`;
    const inbox = `/api/devices/${device_id}/messages`;
    expect((await app.inject({ method: "POST", url: send, payload: { text: "Hello" } })).statusCode).toBe(401);
    expect((await app.inject({ method: "POST", url: send, headers: admin, payload: { text: " " } })).statusCode).toBe(400);
    const message = await app.inject({ method: "POST", url: send, headers: admin, payload: { text: "Dinner", ttl_min: 1 } });
    expect(message.statusCode).toBe(200);
    const id = message.json().id;
    expect((await app.inject({ method: "GET", url: inbox })).statusCode).toBe(401);
    const queue = await app.inject({ method: "GET", url: inbox, headers: child });
    expect(queue.json().messages[0].text).toBe("Dinner");
    expect(queue.json().messages[0].status).toBe("pending");
    for (const status of ["delivered", "confirmed", "delivered"]) {
      expect((await app.inject({ method: "POST", url: `${inbox}/${id}`, headers: child, payload: { status } })).statusCode).toBe(200);
    }
    expect((await app.inject({ method: "GET", url: inbox, headers: child })).json().messages).toEqual([]);
    const overview = await app.inject({ method: "GET", url: "/api/admin/overview", headers: admin });
    expect(overview.json().devices[0].messages[0].status).toBe("confirmed");
    await app.inject({ method: "POST", url: send, headers: admin, payload: { text: "Expires", ttl_min: 1 } });
    const clock = vi.spyOn(Date, "now").mockReturnValue(Date.now() + 61000);
    try {
      expect((await app.inject({ method: "GET", url: inbox, headers: child })).json().messages).toEqual([]);
      const expired = await app.inject({ method: "GET", url: "/api/admin/overview", headers: admin });
      expect(expired.json().devices[0].messages[1].status).toBe("expired");
    } finally { clock.mockRestore(); }
  });

  it("wrong token -> 401", async () => {
    const reg = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "pc-x" } });
    const { device_id } = reg.json();
    const r = await app.inject({ method: "GET", url: `/api/config/${device_id}`, headers: { "x-device-token": "bad" } });
    expect(r.statusCode).toBe(401);
  });

  it("admin can delete device", async () => {
    const reg = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "pc-del" } });
    const { device_id } = reg.json();
    const cred = Buffer.from("admin:test-admin").toString("base64");
    const auth = { authorization: `Basic ${cred}` };

    const del = await app.inject({ method: "DELETE", url: `/api/admin/devices/${device_id}`, headers: auth });
    expect(del.statusCode).toBe(200);
    expect(del.json()).toEqual({ ok: true });

    const again = await app.inject({ method: "DELETE", url: `/api/admin/devices/${device_id}`, headers: auth });
    expect(again.statusCode).toBe(404);

    const ov = await app.inject({ method: "GET", url: "/api/admin/overview", headers: auth });
    expect(ov.json().devices.some((d: { device_id: string }) => d.device_id === device_id)).toBe(false);

    const bad = await app.inject({ method: "DELETE", url: "/api/admin/devices/..%2Fevil", headers: auth });
    expect(bad.statusCode).toBe(400);

    const noAuth = await app.inject({ method: "DELETE", url: `/api/admin/devices/${device_id}` });
    expect(noAuth.statusCode).toBe(401);
  });

  it("config change re-snapshots today quota without new heartbeat", async () => {
    const reg = await app.inject({ method: "POST", url: "/api/register", payload: { hostname: "pc-quota" } });
    const { device_id, token } = reg.json();
    const cred = Buffer.from("admin:test-admin").toString("base64");
    const auth = { authorization: `Basic ${cred}` };
    const todayStr = localDate();

    await app.inject({
      method: "POST", url: "/api/heartbeat", headers: { "x-device-token": token },
      payload: { device_id, date: todayStr, active_min: 10, locked: false },
    });
    const before = await app.inject({ method: "GET", url: "/api/admin/overview", headers: auth });
    expect(before.json().devices.find((d: { device_id: string }) => d.device_id === device_id).today.quota).toBe("1440m");

    const days: Record<string, unknown> = {};
    for (let i = 1; i <= 7; i++) days[String(i)] = { limit_min: 120, windows: [] };
    const put = await app.inject({
      method: "PUT", url: `/api/admin/devices/${device_id}`, headers: { ...auth, "content-type": "application/json" },
      payload: { days },
    });
    expect(put.statusCode).toBe(200);

    const after = await app.inject({ method: "GET", url: "/api/admin/overview", headers: auth });
    const today = after.json().devices.find((d: { device_id: string }) => d.device_id === device_id).today;
    expect(today.quota).toBe("120m");
    expect(today.active_min).toBe(10);
  });
});
