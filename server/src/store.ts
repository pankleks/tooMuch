import { promises as fs } from "node:fs";
import * as path from "node:path";
import { randomBytes } from "node:crypto";
import { fileURLToPath } from "node:url";
import { DeviceFile, ServerConfig, UsageEntry, defaultConfig, normalizeHostname } from "./types.js";
import { localDate } from "./clock.js";

export function dataDir(): string {
  return process.env.DATA_DIR ?? fileURLToPath(new URL("../../data", import.meta.url));
}

export function distDir(): string {
  return process.env.DIST_DIR ?? path.join(dataDir(), "dist");
}

function filePath(dir: string, deviceId: string): string {
  if (!/^[a-z0-9][a-z0-9-]*$/.test(deviceId)) throw new Error("bad device id");
  return path.join(dir, `${deviceId}.json`);
}

export async function ensureDir(dir: string): Promise<void> {
  await fs.mkdir(dir, { recursive: true });
}

/** Atomic write: tmp + rename. */
export async function saveDevice(dir: string, device: DeviceFile): Promise<void> {
  await ensureDir(dir);
  // trim usage to last 30 entries by date
  const keys = Object.keys(device.usage).sort();
  if (keys.length > 30) {
    const keep = new Set(keys.slice(-30));
    for (const k of keys) if (!keep.has(k)) delete device.usage[k];
  }
  if (device.daily_bonuses) {
    const bonusDates = Object.keys(device.daily_bonuses).sort();
    if (bonusDates.length > 30) {
      const keep = new Set(bonusDates.slice(-30));
      for (const date of bonusDates) if (!keep.has(date)) delete device.daily_bonuses[date];
    }
  }
  const tmp = path.join(dir, `.${device.device_id}.${randomBytes(12).toString("hex")}.tmp`);
  await fs.writeFile(tmp, JSON.stringify(device, null, 2), "utf-8");
  await fs.rename(tmp, filePath(dir, device.device_id));
}

export async function loadDevice(dir: string, deviceId: string): Promise<DeviceFile | null> {
  try {
    const raw = await fs.readFile(filePath(dir, deviceId), "utf-8");
    return JSON.parse(raw) as DeviceFile;
  } catch (e: unknown) {
    if ((e as NodeJS.ErrnoException)?.code === "ENOENT") return null;
    throw e;
  }
}

export async function listDevices(dir: string): Promise<DeviceFile[]> {
  await ensureDir(dir);
  const files = await fs.readdir(dir);
  const out: DeviceFile[] = [];
  for (const f of files) {
    if (!f.endsWith(".json") || f.startsWith(".")) continue;
    if (f === "version.json") continue;
    try {
      const raw = await fs.readFile(path.join(dir, f), "utf-8");
      const d = JSON.parse(raw) as DeviceFile;
      if (d && d.device_id) out.push(d);
    } catch {
      // skip corrupt file
    }
  }
  return out.sort((a, b) => (a.device_id < b.device_id ? -1 : 1));
}

/** Delete a device file. Returns false when it does not exist. */
export async function deleteDevice(dir: string, deviceId: string): Promise<boolean> {
  try {
    await fs.unlink(filePath(dir, deviceId));
    return true;
  } catch (e: unknown) {
    if ((e as NodeJS.ErrnoException)?.code === "ENOENT") return false;
    throw e;
  }
}

export interface RegisterResult {
  created: boolean;
  device: DeviceFile;
}

/** Auto-register: create default (no limits) device on first contact. */
export async function registerDevice(
  dir: string,
  hostname: string,
  opts?: { allowAutoRegister?: boolean }
): Promise<RegisterResult> {
  if (opts?.allowAutoRegister === false) throw Object.assign(new Error("auto-register disabled"), { statusCode: 403 });
  const base = normalizeHostname(hostname || "pc");
  await ensureDir(dir);
  const existing = await loadDevice(dir, base);
  if (!existing) {
    const device: DeviceFile = {
      device_id: base,
      name: hostname?.slice(0, 80) || base,
      token: randomBytes(16).toString("hex"),
      last_seen: new Date().toISOString(),
      config: defaultConfig(),
      usage: {},
    };
    await saveDevice(dir, device);
    return { created: true, device };
  }
  // collision: same hostname, device already exists -> do NOT overwrite, suggest suffix
  const suffix = randomBytes(2).toString("hex");
  const altId = `${base}-${suffix}`;
  const device: DeviceFile = {
    device_id: altId,
    name: `${hostname?.slice(0, 80) || base} (${suffix})`,
    token: randomBytes(16).toString("hex"),
    last_seen: new Date().toISOString(),
    config: defaultConfig(),
    usage: {},
  };
  await saveDevice(dir, device);
  return { created: true, device };
}

export function dailyBonusMin(device: DeviceFile, date: string): number {
  const bonus = device.daily_bonuses?.[date] ?? 0;
  return Number.isInteger(bonus) && bonus > 0 ? bonus : 0;
}

export function snapshotQuota(
  day: { limit_min: number; windows: { from: string; to: string }[] },
  mode: "limit" | "window",
  bonusMin = 0
): string {
  if (mode === "window") return day.windows.map((w) => `${w.from}-${w.to}`).join(",");
  return `${day.limit_min + bonusMin}m`;
}

export function weekdayOf(dateStr: string): string {
  // dateStr YYYY-MM-DD -> ISO weekday 1..7 using UTC to avoid TZ drift (client sends local date already)
  const d = new Date(dateStr + "T12:00:00Z");
  const js = d.getUTCDay(); // 0=Sun
  return String(js === 0 ? 7 : js);
}

export async function recordHeartbeat(
  dir: string,
  device: DeviceFile,
  entry: { date: string; active_min: number; locked: boolean; counting?: boolean }
): Promise<DeviceFile> {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(entry.date)) throw Object.assign(new Error("bad date"), { statusCode: 400 });
  const wd = weekdayOf(entry.date);
  const day = device.config.days[wd] ?? { limit_min: 1440, windows: [] };
  const mode: "limit" | "window" = day.windows.length > 0 ? "window" : "limit";
  const prev: UsageEntry | undefined = device.usage[entry.date];
  const active_min = Math.max(prev?.active_min ?? 0, Math.max(0, Math.floor(entry.active_min)));
  device.usage[entry.date] = { active_min, mode, quota: snapshotQuota(day, mode, dailyBonusMin(device, entry.date)) };
  device.last_seen = new Date().toISOString();
  device.locked = entry.locked;
  // Older clients omit this field; clear stale status rather than retaining it.
  if (entry.counting === undefined) delete device.counting;
  else device.counting = entry.counting;
  await saveDevice(dir, device);
  return device;
}

export function applyConfigUpdate(device: DeviceFile, patch: Partial<ServerConfig>): { error: string | null } {
  if (patch.days !== undefined) {
    // validated by caller via validateDays
    device.config.days = patch.days as ServerConfig["days"];
  }
  if (typeof patch.force_lock === "boolean") device.config.force_lock = patch.force_lock;
  if (typeof patch.idle_threshold_sec === "number") {
    if (!Number.isInteger(patch.idle_threshold_sec) || patch.idle_threshold_sec < 30 || patch.idle_threshold_sec > 3600)
      return { error: "idle_threshold_sec must be 30..3600" };
    device.config.idle_threshold_sec = patch.idle_threshold_sec;
  }
  device.config.version += 1;
  return { error: null };
}

/**
 * Recompute today's usage quota snapshot after a config change, so
 * /api/admin/overview shows the new limit immediately instead of waiting
 * for the next heartbeat (which re-snapshots at most every 60s).
 */
export function resnapshotToday(device: DeviceFile): void {
  const today = localDate();
  const entry = device.usage[today];
  if (!entry) return;
  const day = device.config.days[weekdayOf(today)] ?? { limit_min: 1440, windows: [] };
  const mode: "limit" | "window" = day.windows.length > 0 ? "window" : "limit";
  entry.mode = mode;
  entry.quota = snapshotQuota(day, mode, dailyBonusMin(device, today));
}
