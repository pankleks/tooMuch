export const WEEKDAYS = ["1", "2", "3", "4", "5", "6", "7"];
export function defaultDays() {
    const days = {};
    for (const d of WEEKDAYS)
        days[d] = { limit_min: 1440, windows: [] };
    return days;
}
export function defaultConfig() {
    return {
        version: 1,
        force_lock: false,
        idle_threshold_sec: 180,
        count_only_active: true,
        days: defaultDays(),
    };
}
const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/;
export function validateDays(days) {
    if (typeof days !== "object" || days === null)
        return "days must be object";
    const d = days;
    for (let i = 1; i <= 7; i++) {
        const key = String(i);
        const v = d[key];
        if (!v || typeof v !== "object")
            return `days.${key} missing`;
        if (!Number.isInteger(v.limit_min) || v.limit_min < 0 || v.limit_min > 1440)
            return `days.${key}.limit_min must be 0..1440`;
        if (!Array.isArray(v.windows))
            return `days.${key}.windows must be array`;
        for (const w of v.windows) {
            if (!w || typeof w.from !== "string" || typeof w.to !== "string")
                return `days.${key}.windows entry invalid`;
            if (!HHMM.test(w.from) || !HHMM.test(w.to))
                return `days.${key}.windows time must be HH:MM`;
            if (w.from >= w.to)
                return `days.${key}.windows requires from < to (no overnight in MVP)`;
        }
        // overlap check
        const sorted = [...v.windows].sort((a, b) => (a.from < b.from ? -1 : 1));
        for (let k = 1; k < sorted.length; k++) {
            if (sorted[k].from < sorted[k - 1].to)
                return `days.${key}.windows overlap`;
        }
    }
    return null;
}
export function normalizeHostname(hostname) {
    const slug = hostname
        .toLowerCase()
        .normalize("NFKD")
        .replace(/[^a-z0-9]+/g, "-")
        .replace(/^-+|-+$/g, "")
        .slice(0, 40);
    return slug || "pc";
}
