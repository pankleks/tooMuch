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
}
