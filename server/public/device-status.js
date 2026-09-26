const STALE_MS = 5 * 60 * 1000;

export function deviceStatus(device, now = Date.now()) {
  const seenAt = device.last_seen ? Date.parse(device.last_seen) : NaN;
  if (!Number.isFinite(seenAt) || now - seenAt > STALE_MS)
    return { dot: "offline", label: "Offline — no check-in for over 5 minutes" };
  if (device.counting === true) return { dot: "counting", label: "Online — timer is counting" };
  if (device.counting === false) return { dot: "paused", label: "Online — timer is not counting" };
  return { dot: "paused", label: "Online — timer status unavailable; update the client" };
}
