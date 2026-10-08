import { formatMinutes, formatQuota, remainingMinutes, usagePercent } from "./format.js";

/** Update live values in place, without replacing focused controls or config inputs. */
export function updateLiveDetails(card, device) {
  const mode = device.today?.mode ?? device.today_mode;
  const quota = device.today?.quota ?? device.today_quota;
  const used = device.today?.active_min ?? 0;
  const remaining = remainingMinutes(mode, quota, used);
  const label = remaining === null ? "—" : formatMinutes(remaining);
  const usage = card.querySelector(".usage-row");
  usage.querySelector(".usage-summary strong").textContent = formatMinutes(used);
  const usedLabel = `Used today: ${formatMinutes(used)}`;
  const usedNode = usage.querySelector(".usage-summary");
  usedNode.title = usedLabel;
  usedNode.setAttribute("aria-label", usedLabel);
  usage.querySelector(".bar i").style.width = `${usagePercent({ mode, quota, active_min: used })}%`;
  usage.querySelector(".bar-label").textContent = label;
  usage.querySelector(".bar").setAttribute("aria-label", remaining === null ? "Remaining time unavailable" : `Remaining time: ${label}`);
  usage.querySelector(".usage-limit strong").textContent = formatQuota(quota ?? "?");
  const limitLabel = `${mode === "window" ? "Allowed today" : "Limit today"}: ${formatQuota(quota ?? "?")}`;
  const limitNode = usage.querySelector(".usage-limit");
  limitNode.title = limitLabel;
  limitNode.setAttribute("aria-label", limitLabel);

  const bonus = card.querySelector('[data-act="daily-bonus"]');
  bonus.disabled = mode === "window";
  bonus.title = bonus.disabled ? "Extra time applies only to daily limits, not time windows" : "Add 15m to today's daily limit";
  bonus.setAttribute("aria-label", bonus.disabled ? "Extra time unavailable for window schedules" : bonus.title);
  const lock = card.querySelector('[data-act="lock"]');
  const lockLabel = device.force_lock ? "Unlock device" : "Lock device now";
  lock.title = lockLabel;
  lock.setAttribute("aria-label", lockLabel);

  const recent = card.querySelector(".recent-messages");
  if (recent) {
    const sorted = [...(device.messages ?? [])]
      .map((message, index) => ({ message, index }))
      .sort((a, b) => (Date.parse(b.message.created_at) || 0) - (Date.parse(a.message.created_at) || 0) || b.index - a.index)
      .slice(0, 5);
    if (!sorted.length) {
      recent.textContent = "";
      recent.title = "";
      recent.tabIndex = -1;
    } else {
      recent.textContent = messageSummary(sorted[0].message);
      recent.title = sorted.map(({ message }) => messageSummary(message)).join("\n");
      recent.tabIndex = 0;
    }
  }
}

function messageStatusLabel(status) {
  return { pending: "Pending", delivered: "Delivered to client", confirmed: "Confirmed (OK)", expired: "Expired" }[status] || status;
}

function messageSentAt(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Unknown time" : new Intl.DateTimeFormat(undefined, { dateStyle: "short", timeStyle: "short" }).format(date);
}

function messageSummary(message) {
  return `${messageSentAt(message.created_at)} — ${message.text} — ${messageStatusLabel(message.status)}`;
}
