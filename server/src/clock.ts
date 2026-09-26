export function timeZone(): string {
  const zone = process.env.TIME_ZONE ?? "Europe/Warsaw";
  // Reject an invalid deployment setting instead of silently changing the date.
  new Intl.DateTimeFormat("en", { timeZone: zone }).format();
  return zone;
}

export function localDate(now = new Date()): string {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: timeZone(), year: "numeric", month: "2-digit", day: "2-digit",
  }).formatToParts(now);
  const get = (type: string) => parts.find(p => p.type === type)!.value;
  return `${get("year")}-${get("month")}-${get("day")}`;
}

export function recentDates(now = new Date()): string[] {
  const today = new Date(`${localDate(now)}T12:00:00Z`);
  return Array.from({ length: 7 }, (_, i) => {
    const day = new Date(today);
    day.setUTCDate(day.getUTCDate() - i);
    return day.toISOString().slice(0, 10);
  });
}
