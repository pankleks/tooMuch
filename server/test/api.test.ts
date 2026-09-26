import { describe, it, expect, beforeEach } from "vitest";
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
      payload: { device_id, date: todayStr, active_min: 45, locked: false },
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
    expect(me.last7.length).toBe(7);
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
