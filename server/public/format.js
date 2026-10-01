/** Formats minute counts as compact durations (e.g. 90 -> "1h 30m"). */
export function formatMinutes(value) {
  const minutes = Math.floor(Number(value));
  if (!Number.isFinite(minutes) || minutes < 0) return "?";
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours === 0) return `${rest}m`;
  if (rest === 0) return `${hours}h`;
  return `${hours}h ${rest}m`;
}

/** Converts stored minute quotas, leaving window strings such as 16:00-20:00 alone. */
export function formatQuota(value) {
  const quota = String(value ?? "");
  const match = /^(\d+)m$/.exec(quota);
  return match ? formatMinutes(Number(match[1])) : (quota || "?");
}

function quotaMinutes(mode, quota) {
  if (mode === "window") {
    const windows = String(quota ?? "").split(",");
    let total = 0;
    for (const window of windows) {
      const match = window.trim().match(/^(\d{2}):(\d{2})-(\d{2}):(\d{2})$/);
      if (!match) return null;
      total += Math.max(0, Number(match[3]) * 60 + Number(match[4]) - Number(match[1]) * 60 - Number(match[2]));
    }
    return total;
  }
  if (mode !== "limit") return null;
  const match = /^(\d+)m$/.exec(String(quota ?? ""));
  return match ? Number(match[1]) : null;
}

/** Minutes remaining against the daily limit or total window duration. */
export function remainingMinutes(mode, quota, used) {
  const limit = quotaMinutes(mode, quota);
  const usedMinutes = Number(used);
  if (limit === null || !Number.isFinite(usedMinutes) || usedMinutes < 0) return null;
  return Math.max(0, Math.floor(limit - usedMinutes));
}

/** Usage percentage against the daily limit or total duration of all windows. */
export function usagePercent(today) {
  if (!today) return 0;
  const limit = quotaMinutes(today.mode, today.quota);
  if (!limit) return today.active_min > 0 ? 100 : 0;
  return Math.min(100, Math.max(0, Math.round(today.active_min / limit * 100)));
}

/** Human-readable age for ISO timestamps from last_seen. */
export function formatTimeAgo(value, now = Date.now()) {
  if (!value) return "never";
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) return "unknown";
  const seconds = Math.max(0, Math.floor((now - timestamp) / 1000));
  if (seconds < 60) return `${seconds} second${seconds === 1 ? "" : "s"} ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? "" : "s"} ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? "" : "s"} ago`;
  const days = Math.floor(hours / 24);
  return `${days} day${days === 1 ? "" : "s"} ago`;
}
