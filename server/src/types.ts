export interface TimeWindow {
  from: string; // HH:MM
  to: string; // HH:MM
}

export interface DayConfig {
  limit_min: number; // 0 = blocked all day, 1440 = effectively unlimited
  windows: TimeWindow[];
}

export type DaysConfig = Record<string, DayConfig>; // keys "1".."7"

export interface ServerConfig {
  version: number;
  force_lock: boolean;
  idle_threshold_sec: number;
  count_only_active: boolean;
  days: DaysConfig;
}

export interface UsageEntry {
  active_min: number;
  mode: "limit" | "window";
  quota: string; // snapshot e.g. "120m" or "16:00-20:00"
}

export interface DeviceFile {
  messages?: ParentMessage[];
  daily_bonuses?: Record<string, number>; // per-date extra daily-limit minutes; never changes the recurring config
  device_id: string;
  name: string;
  token: string; // hex, stored plain on server (LAN home use); clients send via X-Device-Token
  last_seen: string | null; // ISO
  locked?: boolean;
  counting?: boolean;
  config: ServerConfig;
  usage: Record<string, UsageEntry>; // key YYYY-MM-DD
}

export interface ParentMessage {
  id: string;
  text: string;
  created_at: string;
  expires_at: string;
  status: "pending" | "delivered" | "confirmed";
}

export const WEEKDAYS = ["1", "2", "3", "4", "5", "6", "7"] as const;

export function defaultDays(): DaysConfig {
  const days: DaysConfig = {};
  for (const d of WEEKDAYS) days[d] = { limit_min: 1440, windows: [] };
  return days;
}

export function defaultConfig(): ServerConfig {
  return {
    version: 1,
    force_lock: false,
    idle_threshold_sec: 180,
    count_only_active: true,
    days: defaultDays(),
  };
}

const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/;

export function validateDays(days: unknown): string | null {
  if (typeof days !== "object" || days === null) return "days must be object";
  const d = days as Record<string, unknown>;
  for (let i = 1; i <= 7; i++) {
    const key = String(i);
    const v = d[key] as DayConfig | undefined;
    if (!v || typeof v !== "object") return `days.${key} missing`;
    if (!Number.isInteger(v.limit_min) || v.limit_min < 0 || v.limit_min > 1440)
      return `days.${key}.limit_min must be 0..1440`;
    if (!Array.isArray(v.windows)) return `days.${key}.windows must be array`;
    for (const w of v.windows) {
      if (!w || typeof w.from !== "string" || typeof w.to !== "string") return `days.${key}.windows entry invalid`;
      if (!HHMM.test(w.from) || !HHMM.test(w.to)) return `days.${key}.windows time must be HH:MM`;
      if (w.from >= w.to) return `days.${key}.windows requires from < to (no overnight in MVP)`;
    }
    // overlap check
    const sorted = [...v.windows].sort((a, b) => (a.from < b.from ? -1 : 1));
    for (let k = 1; k < sorted.length; k++) {
      if (sorted[k].from < sorted[k - 1].to) return `days.${key}.windows overlap`;
    }
  }
  return null;
}

export function normalizeHostname(hostname: string): string {
  const slug = hostname
    .toLowerCase()
    .normalize("NFKD")
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 40);
  return slug || "pc";
}
