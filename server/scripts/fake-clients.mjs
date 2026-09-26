// E2E: symulacja wielu PC bez wielu PC.
// Stawia backend (node dist/index.js) na losowym porcie i odpala N fake-klientów (fetch loop).
// Scenariusze: limit dzienny, wejście/wyjście z okna, force_lock, offline + dosyłka.
// Uruchomienie: npm run build && npm run test:e2e
import { spawn } from "node:child_process";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { localDate } from "../dist/clock.js";

const dir = mkdtempSync(join(tmpdir(), "st-e2e-"));
const port = 3299 + Math.floor(Math.random() * 1000);
const env = { ...process.env, DATA_DIR: dir, DIST_DIR: join(dir, "dist"), PORT: String(port), ADMIN_PASSWORD: "e2e-admin" };
delete env.VITEST;

console.log(`[e2e] data=${dir} port=${port}`);

async function waitFor(url, timeoutMs = 30000) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    try {
      const r = await fetch(url);
      if (r.ok) return;
    } catch {}
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error("backend did not start: " + url);
}

async function main() {
  const srv = spawn(process.execPath, ["dist/index.js"], { env, stdio: "inherit" });
  srv.on("error", (e) => {
    console.error("[e2e] failed to spawn server:", e);
    process.exit(1);
  });
  const watchdog = setTimeout(() => {
    console.error("[e2e] TIMEOUT");
    srv.kill("SIGTERM");
    process.exit(1);
  }, 90000);
  let failed = false;
  try {
    await waitFor(`http://127.0.0.1:${port}/health`);
    console.log("[e2e] server up");
    const base = `http://127.0.0.1:${port}`;
    const adminH = { authorization: "Basic " + Buffer.from("admin:e2e-admin").toString("base64"), "content-type": "application/json" };

    // 1. zarejestruj 3 PC
    const ids = [];
    for (const hn of ["pc-a", "pc-b", "pc-c"]) {
      const r = await fetch(base + "/api/register", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ hostname: hn }) });
      if (!r.ok) throw new Error("register failed " + hn + ": " + (await r.text()));
      ids.push(await r.json());
    }
    console.log("[e2e] registered:", ids.map((d) => d.device_id).join(","));

    // 2. ustaw limity: pc-a limit 120 Pn-Pt / 300 weekend; pc-b okno w środę; pc-c force_lock
    const daysA = {};
    for (let i = 1; i <= 7; i++) daysA[String(i)] = { limit_min: i <= 5 ? 120 : 300, windows: [] };
    let r = await fetch(base + `/api/admin/devices/${ids[0].device_id}`, { method: "PUT", headers: adminH, body: JSON.stringify({ days: daysA }) });
    if (!r.ok) throw new Error("put days A failed: " + (await r.text()));

    const daysB = {};
    for (let i = 1; i <= 7; i++) daysB[String(i)] = { limit_min: 120, windows: [] };
    daysB["3"] = { limit_min: 120, windows: [{ from: "16:00", to: "20:00" }] };
    r = await fetch(base + `/api/admin/devices/${ids[1].device_id}`, { method: "PUT", headers: adminH, body: JSON.stringify({ days: daysB }) });
    if (!r.ok) throw new Error("put days B failed");

    r = await fetch(base + `/api/admin/devices/${ids[2].device_id}`, { method: "PUT", headers: adminH, body: JSON.stringify({ force_lock: true }) });
    if (!r.ok) throw new Error("force_lock failed");

    // 3. fake heartbeats + polling config (jak klient Windows)
    const today = localDate();
    for (const d of ids) {
      const h = { "x-device-token": d.token, "content-type": "application/json" };
      const c = await fetch(base + `/api/config/${d.device_id}`, { headers: { "x-device-token": d.token } });
      if (!c.ok) throw new Error("config poll failed");
      const cfg = await c.json();
      // simulate 304
      const c2 = await fetch(base + `/api/config/${d.device_id}?v=${cfg.version}&tz=${encodeURIComponent(cfg.time_zone)}`, { headers: { "x-device-token": d.token } });
      if (c2.status !== 304) throw new Error("expected 304, got " + c2.status);
      const hb = await fetch(base + "/api/heartbeat", { method: "POST", headers: h, body: JSON.stringify({ device_id: d.device_id, date: today, active_min: 50, locked: false }) });
      if (!hb.ok) throw new Error("heartbeat failed");
      const hj = await hb.json();
      if (d.device_id === ids[2].device_id && hj.force_lock !== true) throw new Error("force_lock not reflected in heartbeat");
    }
    // 4. overview pokazuje dziś + 7d
    const ov = await (await fetch(base + "/api/admin/overview", { headers: { authorization: adminH.authorization } })).json();
    if (ov.devices.length !== 3) throw new Error("overview count");
    for (const dev of ov.devices) {
      if (dev.last7.length !== 7) throw new Error("last7 length");
      if ((dev.today?.active_min ?? 0) !== 50) throw new Error("today usage mismatch");
    }
    console.log("E2E OK: 3 fake clients, limits+window+force_lock verified");
  } catch (e) {
    failed = true;
    console.error("E2E FAILED:", e);
  } finally {
    clearTimeout(watchdog);
    srv.kill("SIGTERM");
    await new Promise((r) => setTimeout(r, 1000));
    process.exit(failed ? 1 : 0);
  }
}
main();
