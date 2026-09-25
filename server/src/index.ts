import Fastify, { FastifyInstance, FastifyReply, FastifyRequest } from "fastify";
import fastifyStatic from "@fastify/static";
import fastifyBasicAuth from "@fastify/basic-auth";
import { promises as fs } from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import {
  dataDir,
  distDir,
  listDevices,
  loadDevice,
  saveDevice,
  deleteDevice,
  registerDevice,
  recordHeartbeat,
  applyConfigUpdate,
} from "./store.js";
import { validateDays } from "./types.js";

const PORT = Number(process.env.PORT ?? 3020);
function adminPassword(): string {
  return process.env.ADMIN_PASSWORD ?? "changeme";
}
function allowAutoRegister(): boolean {
  return (process.env.ALLOW_AUTO_REGISTER ?? "true") === "true";
}

function adminAuthHash(u: string, p: string): boolean {
  const ADMIN_PASSWORD = adminPassword();
  // single admin user; constant-time-ish compare for home use
  if (u !== "admin") return false;
  if (p.length !== ADMIN_PASSWORD.length) return false;
  let diff = 0;
  for (let i = 0; i < p.length; i++) diff |= p.charCodeAt(i) ^ ADMIN_PASSWORD.charCodeAt(i);
  return diff === 0;
}

async function deviceAuth(req: FastifyRequest, deviceId: string): Promise<ReturnType<typeof loadDevice> extends Promise<infer T> ? T : never> {
  const dir = dataDir();
  const device = await loadDevice(dir, deviceId);
  if (!device) return null;
  const token = (req.headers["x-device-token"] as string) ?? "";
  if (token !== device.token) return null;
  return device;
}

export async function buildApp(): Promise<FastifyInstance> {
  const app = Fastify({ logger: false });

  await app.register(fastifyBasicAuth, {
    validate: async (username: string, password: string) => {
      if (!adminAuthHash(username, password)) throw new Error("unauthorized");
    },
    authenticate: true,
  });

  const here = path.dirname(fileURLToPath(import.meta.url));
  const publicDir = path.join(here, "..", "public");
  await app.register(fastifyStatic, { root: publicDir, prefix: "/admin-static/" });

  app.get("/health", async () => ({ ok: true }));

  // ---- device: auto-register (no token, LAN) ----
  app.post(
    "/api/register",
    {
      schema: {
        body: {
          type: "object",
          required: ["hostname"],
          properties: { hostname: { type: "string", minLength: 1, maxLength: 80 } },
        },
      },
    },
    async (req, reply) => {
      if (!allowAutoRegister()) return reply.code(403).send({ error: "auto-register disabled" });
      const { hostname } = req.body as { hostname: string };
      const { device } = await registerDevice(dataDir(), hostname, { allowAutoRegister: true });
      return reply.send({ device_id: device.device_id, token: device.token });
    }
  );

  // ---- device: poll config ----
  app.get("/api/config/:id", async (req: FastifyRequest<{ Params: { id: string }; Querystring: { v?: string } }>, reply: FastifyReply) => {
    const device = await deviceAuth(req, req.params.id);
    if (!device) return reply.code(401).send({ error: "unauthorized" });
    const v = Number(req.query.v ?? -1);
    if (v === device.config.version) return reply.code(304).send();
    return reply.send({
      device_id: device.device_id,
      version: device.config.version,
      force_lock: device.config.force_lock,
      idle_threshold_sec: device.config.idle_threshold_sec,
      count_only_active: device.config.count_only_active,
      days: device.config.days,
    });
  });

  // ---- device: heartbeat / usage ----
  app.post(
    "/api/heartbeat",
    {
      schema: {
        body: {
          type: "object",
          required: ["device_id", "date", "active_min"],
          properties: {
            device_id: { type: "string" },
            date: { type: "string", pattern: "^\\d{4}-\\d{2}-\\d{2}$" },
            active_min: { type: "number", minimum: 0, maximum: 100000 },
            locked: { type: "boolean" },
          },
        },
      },
    },
    async (req, reply) => {
      const body = req.body as { device_id: string; date: string; active_min: number; locked?: boolean };
      const device = await deviceAuth(req, body.device_id);
      if (!device) return reply.code(401).send({ error: "unauthorized" });
      await recordHeartbeat(dataDir(), device, {
        date: body.date,
        active_min: body.active_min,
        locked: body.locked ?? false,
      });
      return reply.send({ ok: true, version: device.config.version, force_lock: device.config.force_lock });
    }
  );

  // ---- public client version (for future auto-update) ----
  app.get("/api/client-version", async (_req, reply) => {
    try {
      const raw = await fs.readFile(path.join(distDir(), "version.json"), "utf-8");
      return reply.header("content-type", "application/json").send(raw);
    } catch {
      return reply.code(404).send({ error: "no client build published" });
    }
  });

  // ---- admin: overview (today + last 7d) ----
  app.get("/api/admin/overview", { onRequest: app.basicAuth }, async (_req, _reply) => {
    const devices = await listDevices(dataDir());
    const today = new Date();
    const days: string[] = [];
    for (let i = 0; i < 7; i++) {
      const d = new Date(today);
      d.setDate(today.getDate() - i);
      days.push(d.toISOString().slice(0, 10));
    }
    return {
      devices: devices.map((d) => ({
        device_id: d.device_id,
        name: d.name,
        last_seen: d.last_seen,
        version: d.config.version,
        force_lock: d.config.force_lock,
        today: d.usage[days[0]] ?? null,
        last7: days.map((date) => ({ date, ...(d.usage[date] ?? { active_min: 0, mode: null, quota: null }) })),
        config: d.config,
      })),
    };
  });

  // ---- admin: update device config ----
  app.put<{ Params: { id: string } }>(
    "/api/admin/devices/:id",
    { onRequest: app.basicAuth },
    async (req, reply) => {
      const device = await loadDevice(dataDir(), req.params.id);
      if (!device) return reply.code(404).send({ error: "not found" });
      const patch = (req.body ?? {}) as { days?: unknown; force_lock?: unknown; idle_threshold_sec?: unknown };
      if (patch.days !== undefined) {
        const err = validateDays(patch.days);
        if (err) return reply.code(400).send({ error: err });
      }
      if (patch.force_lock !== undefined && typeof patch.force_lock !== "boolean")
        return reply.code(400).send({ error: "force_lock must be boolean" });
      const res = applyConfigUpdate(device, patch as never);
      if (res.error) return reply.code(400).send({ error: res.error });
      await saveDevice(dataDir(), device);
      return reply.send({ ok: true, version: device.config.version, config: device.config });
    }
  );

  // ---- admin: delete device ----
  app.delete<{ Params: { id: string } }>(
    "/api/admin/devices/:id",
    { onRequest: app.basicAuth },
    async (req, reply) => {
      let deleted: boolean;
      try {
        deleted = await deleteDevice(dataDir(), req.params.id);
      } catch {
        return reply.code(400).send({ error: "bad device id" });
      }
      if (!deleted) return reply.code(404).send({ error: "not found" });
      return reply.send({ ok: true });
    }
  );

  // ---- admin: download installer ----
  app.get("/api/admin/client-download", { onRequest: app.basicAuth }, async (_req, reply) => {
    const zip = path.join(distDir(), "tooMuch-win-x64.zip");
    try {
      await fs.access(zip);
    } catch {
      return reply.code(404).send({ error: "no client build published (copy zip to DIST_DIR)" });
    }
    return reply.header("content-disposition", 'attachment; filename="tooMuch-win-x64.zip"').sendFile("tooMuch-win-x64.zip", distDir());
  });

  // serve admin page at /admin
  app.get("/admin", async (_req, reply) => {
    return reply.sendFile("admin.html", publicDir);
  });

  return app;
}

if (process.env.VITEST !== "true" && import.meta.url === `file://${process.argv[1]}`) {
  const app = await buildApp();
  if (adminPassword() === "changeme") {
    console.warn("[warn] ADMIN_PASSWORD not set, using default 'changeme' – set it in .env");
  }
  await app.listen({ port: PORT, host: "0.0.0.0" });
  console.log(`tooMuch-server listening on :${PORT}`);
}
