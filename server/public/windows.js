export function parseWindows(value) {
  if (!value.trim()) return [];
  return value.split(",").map(part => {
    const match = part.trim().match(/^(\d{1,2}:[0-5]\d)\s*-\s*(\d{1,2}:[0-5]\d)$/);
    if (!match) throw new Error("Bad window format; use 8:00-10:00");
    return { from: match[1].padStart(5, "0"), to: match[2].padStart(5, "0") };
  });
}
