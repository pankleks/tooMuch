import { formatMinutes, formatQuota, remainingMinutes, usagePercent } from "./format.js";
import { deviceStatus } from "./device-status.js";
import { parseWindows } from "./windows.js";

const DAY_NAMES = {1:"Mon",2:"Tue",3:"Wed",4:"Thu",5:"Fri",6:"Sat",7:"Sun"};
// Lucide icons (ISC-licensed), embedded locally so the admin PWA works offline.
const ICONS = {
  lock: '<svg aria-hidden="true" viewBox="0 0 24 24"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>',
  unlock: '<svg aria-hidden="true" viewBox="0 0 24 24"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/></svg>',
  message: '<svg aria-hidden="true" viewBox="0 0 24 24"><path d="M21 15a4 4 0 0 1-4 4H7l-4 4V7a4 4 0 0 1 4-4h10a4 4 0 0 1 4 4z"/></svg>',
  edit: '<svg aria-hidden="true" viewBox="0 0 24 24"><path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L8 18l-4 1 1-4Z"/></svg>',
  save: '<svg aria-hidden="true" viewBox="0 0 24 24"><path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2Z"/><path d="M17 21v-8H7v8"/><path d="M7 3v5h8"/></svg>',
  copy: '<svg aria-hidden="true" viewBox="0 0 24 24"><rect x="8" y="8" width="14" height="14" rx="2"/><path d="M4 16H3a2 2 0 0 1-2-2V3a2 2 0 0 1 2-2h11a2 2 0 0 1 2 2v1"/></svg>',
  check: '<svg aria-hidden="true" viewBox="0 0 24 24"><path d="m5 12 4 4L19 6"/></svg>',
  trash: '<svg aria-hidden="true" viewBox="0 0 24 24"><path d="M3 6h18"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><path d="M10 11v6"/><path d="M14 11v6"/></svg>'
};
let devices = [];
const drafts = new Map();
const detailPanels = new Map();
let renderedDeviceId = null;
let refreshSequence = 0;
let forceDetailRefresh = false;
let dailyBonusRefocus = null;

async function api(path, opts={}) {
  const r = await fetch(path, opts);
  if (r.status === 401) { document.getElementById("status").textContent = "401 – enter the admin password (BasicAuth)"; throw new Error("auth"); }
  const t = await r.text();
  let j = null; try { j = t ? JSON.parse(t) : null; } catch { j = {raw:t}; }
  if (!r.ok) throw new Error((j && j.error) || r.statusText);
  return j;
}

async function loadVersion() {
  try {
    const v = await api("/api/client-version");
    document.getElementById("ver").textContent = `version ${v.version} sha ${String(v.sha256||"").slice(0,12)}`;
  } catch (e) {
    const status = document.getElementById("ver");
    status.textContent = e.message === "no client build published"
      ? "version.json missing in /data/dist; ZIP download may still be available"
      : `version info unavailable: ${e.message}`;
  }
}

function todaySummary(d) {
  const t = d.today;
  if (!t) return "no data";
  return `${formatMinutes(t.active_min)} / ${formatQuota(t.quota)}`;
}

function messageStatusLabel(status) {
  return {pending:"Pending",delivered:"Delivered to client",confirmed:"Confirmed (OK)",expired:"Expired"}[status] || status;
}

function messageSentAt(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Unknown time" : new Intl.DateTimeFormat(undefined, {dateStyle:"short",timeStyle:"short"}).format(date);
}

function messageSummary(message) {
  return `${messageSentAt(message.created_at)} — ${message.text} — ${messageStatusLabel(message.status)}`;
}

async function copyTextToClipboard(text) {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return;
    } catch { /* try the legacy fallback below */ }
  }
  const field = document.createElement("textarea");
  field.value = text;
  field.setAttribute("readonly", "");
  field.style.position = "fixed";
  field.style.left = "-10000px";
  document.body.appendChild(field);
  field.select();
  const copied = document.execCommand("copy");
  field.remove();
  if (!copied) throw new Error("Clipboard copy failed");
}

function selectedId() {
  const h = (location.hash||"").replace(/^#/,"");
  if (h && devices.some(d=>d.device_id===h)) return h;
  const saved = localStorage.getItem("toomuch-selected");
  if (saved && devices.some(d=>d.device_id===saved)) return saved;
  return devices.length ? devices[0].device_id : null;
}

function select(id) {
  location.hash = id;
  localStorage.setItem("toomuch-selected", id);
  renderTabs();
  renderDetail();
}

function renderTabs() {
  const tabs = document.getElementById("tabs");
  tabs.innerHTML = "";
  const sel = selectedId();
  for (const d of devices) {
    const b = document.createElement("button");
    b.className = "tab" + (d.device_id===sel ? " active" : "") + (d.force_lock ? " locked" : "");
    const status = deviceStatus(d);
    b.innerHTML = `<span class="dot ${status.dot}"></span><strong>${esc(d.name)}</strong><span class="tmin">${esc(todaySummary(d))}</span>`;
    b.title = `${d.device_id} — ${status.label}\nClient version: ${d.client_version ? `v${d.client_version}` : "not reported"}${d.force_lock ? "\nParent lock is enabled" : ""}`;
    b.onclick = ()=>select(d.device_id);
    tabs.appendChild(b);
  }
}

function renderDetail() {
  const list = document.getElementById("list");
  const d = devices.find(x=>x.device_id===selectedId());
  const nextDeviceId = d?.device_id ?? null;
  if (renderedDeviceId && renderedDeviceId !== nextDeviceId) drafts.delete(renderedDeviceId);
  renderedDeviceId = nextDeviceId;
  list.innerHTML = "";
  if (!d) { list.innerHTML = `<p class="muted">No devices yet. Install the client on a PC – it will auto-register here.</p>`; return; }
  list.appendChild(buildCard(d));
}

async function refresh(forceDetail = false, refocusDailyBonus = null) {
  if (forceDetail) {
    forceDetailRefresh = true;
    dailyBonusRefocus = refocusDailyBonus;
  }
  const sequence = ++refreshSequence;
  const st = document.getElementById("status");
  st.textContent = "loading…";
  try {
    const ov = await api("/api/admin/overview");
    if (sequence !== refreshSequence) return;
    devices = ov.devices || [];
    st.textContent = "";
    renderTabs();
    if (forceDetailRefresh || !document.querySelector("#list :focus")) {
      renderDetail();
      if (forceDetailRefresh) {
        forceDetailRefresh = false;
        const target = dailyBonusRefocus;
        dailyBonusRefocus = null;
        if (target && selectedId() === target.deviceId) {
          document.querySelector(`[data-act="daily-bonus"][data-minutes="${target.minutes}"]`)?.focus();
        }
      }
    }
  } catch(e){
    st.textContent = navigator.onLine ? "server unavailable: " + e.message : "offline — reconnect to the tooMuch server";
    if (!devices.length) document.getElementById("list").innerHTML = `<p class="muted">The admin panel is available offline, but device data and changes require a connection to the server.</p>`;
  }
}

function buildCard(d) {
  const today = d.today;
  const recentMessages = (d.messages || []).map((message, index) => ({message, index}))
    .sort((a, b) => (Date.parse(b.message.created_at) || 0) - (Date.parse(a.message.created_at) || 0) || b.index - a.index)
    .slice(0, 5);
  const card = document.createElement("div");
  card.className = "card device-detail";
  const activePanel = detailPanels.get(d.device_id) ?? "history";
  const lockLabel = d.force_lock ? "Unlock device" : "Lock device now";
  const todayMode = today?.mode ?? d.today_mode;
  const todayQuota = today?.quota ?? d.today_quota;
  const onWindowDay = todayMode === "window";
  const usedToday = today?.active_min ?? 0;
  const progressToday = today ?? {active_min:usedToday, mode:todayMode, quota:todayQuota};
  const remainingToday = remainingMinutes(todayMode, todayQuota, usedToday);
  const remainingLabel = remainingToday === null ? "—" : formatMinutes(remainingToday);
  const remainingAriaLabel = remainingToday === null ? "Remaining time unavailable" : `Remaining time: ${remainingLabel}`;
  card.innerHTML = `
    <div class="row usage-row">
      <span class="usage-summary" title="Used today: ${formatMinutes(usedToday)}" aria-label="Used today: ${formatMinutes(usedToday)}"><strong>${formatMinutes(usedToday)}</strong></span>
      <div class="bar" role="img" aria-label="${remainingAriaLabel}"><i style="width:${barWidth(progressToday)}%"></i><span class="bar-label" aria-hidden="true">${esc(remainingLabel)}</span></div>
      <span class="usage-summary usage-limit" title="${onWindowDay ? "Allowed today" : "Limit today"}: ${esc(formatQuota(todayQuota ?? "?"))}" aria-label="${onWindowDay ? "Allowed today" : "Limit today"}: ${esc(formatQuota(todayQuota ?? "?"))}"><strong>${esc(formatQuota(todayQuota ?? "?"))}</strong></span>
    </div>
    <div class="row action-row">
      <button type="button" class="icon-button" data-act="lock" aria-label="${lockLabel}" title="${lockLabel}">${d.force_lock?ICONS.unlock:ICONS.lock}</button>
      <button type="button" class="icon-button" data-act="message" aria-label="Send message to child" title="Send message to child">${ICONS.message}</button>
      <button type="button" class="icon-button" data-act="daily-bonus" data-minutes="15" aria-label="${onWindowDay ? "Extra time unavailable for window schedules" : "Add 15m to today's daily limit"}" title="${onWindowDay ? "Extra time applies only to daily limits, not time windows" : "Add 15m to today's daily limit"}" ${onWindowDay?"disabled":""}>+15m</button>
      <button type="button" class="icon-button" data-act="rename" aria-label="Rename device" title="Rename device">${ICONS.edit}</button>
      <button type="button" class="icon-button danger" data-act="del" aria-label="Delete device" title="Delete device">${ICONS.trash}</button>
    </div>
    <div class="muted recent-messages"${recentMessages.length ? ' tabindex="0"' : ""}></div>
    <div class="detail-tabs" role="tablist" aria-label="Device details">
      <button type="button" class="detail-tab" role="tab" id="history-tab" aria-controls="history-panel" data-panel="history">History</button>
      <button type="button" class="detail-tab" role="tab" id="config-tab" aria-controls="config-panel" data-panel="config">Config</button>
    </div>
    <section class="detail-panel" id="history-panel" role="tabpanel" aria-labelledby="history-tab" tabindex="0" data-panel-content="history">
    <table class="responsive-table history-table"><thead><tr><th scope="col">Date</th><th scope="col">Mode</th><th scope="col">Quota</th><th scope="col">Used</th></tr></thead><tbody>
    ${d.last7.map(x=>`<tr><td><span class="mobile-label">Date</span>${esc(x.date)}</td><td><span class="mobile-label">Mode</span>${esc(x.mode??"-")}</td><td><span class="mobile-label">Quota</span>${esc(formatQuota(x.quota??"-"))}</td><td><span class="mobile-label">Used</span>${formatMinutes(x.active_min)}</td></tr>`).join("")}
    </tbody></table>
    </section>
    <section class="detail-panel" id="config-panel" role="tabpanel" aria-labelledby="config-tab" tabindex="0" data-panel-content="config">
    <table class="responsive-table schedule-table"><thead><tr><th scope="col">Day</th><th scope="col">Limit [min]</th><th scope="col">Windows</th><th scope="col">Mode</th></tr></thead><tbody>
    ${[1,2,3,4,5,6,7].map(n=>{
      const c = d.config.days[String(n)];
      const draft = drafts.get(d.device_id)?.[String(n)];
      return `<tr><td><span class="mobile-label">Day</span>${DAY_NAMES[n]}</td>
      <td><span class="mobile-label">Limit [min]</span><input type="number" min="0" max="1440" aria-label="${DAY_NAMES[n]} daily limit in minutes" data-day="${n}" data-f="limit" value="${esc(draft?.limit ?? c.limit_min)}"></td>
      <td><span class="mobile-label">Windows</span><input type="text" aria-label="${DAY_NAMES[n]} allowed windows" data-day="${n}" data-f="windows" value="${esc(draft?.windows ?? (c.windows||[]).map(w=>w.from+"-"+w.to).join(", "))}" placeholder="empty = limit mode"></td>
      <td><span class="mobile-label">Mode</span><span class="mode-value" data-day="${n}">${(c.windows&&c.windows.length)?"WINDOW":"LIMIT"}</span></td></tr>`;
    }).join("")}
    </tbody></table>
    <div class="row">
      <button type="button" class="icon-button" data-act="save" aria-label="Save days" title="Save days">${ICONS.save}</button>
      <span class="muted action-help">Windows override the limit. Use 16:00-20:00, comma-separated for multiple. 0 = blocked.</span>
    </div>
    </section>`;
  if (recentMessages.length) {
    const latestMessage = recentMessages[0].message;
    const recentMessageElement = card.querySelector(".recent-messages");
    recentMessageElement.textContent = messageSummary(latestMessage);
    recentMessageElement.title = recentMessages.map(({message}) => messageSummary(message)).join("\n");
  }
  detailPanels.set(d.device_id, activePanel);
  const detailTablist = card.querySelector(".detail-tabs");
  let currentPanel = activePanel;
  const discardConfigDraft = () => {
    drafts.delete(d.device_id);
    for (let n = 1; n <= 7; n++) {
      const day = d.config.days[String(n)];
      const limit = card.querySelector(`input[data-day="${n}"][data-f="limit"]`);
      const windows = card.querySelector(`input[data-day="${n}"][data-f="windows"]`);
      const hasWindows = Boolean(day.windows?.length);
      limit.value = String(day.limit_min);
      windows.value = (day.windows || []).map(w => `${w.from}-${w.to}`).join(", ");
      limit.disabled = hasWindows;
      card.querySelector(`.mode-value[data-day="${n}"]`).textContent = hasWindows ? "WINDOW" : "LIMIT";
    }
  };
  const setPanel = panel => {
    if (currentPanel === "config" && panel !== "config") discardConfigDraft();
    currentPanel = panel;
    detailPanels.set(d.device_id, panel);
    for (const tab of detailTablist.querySelectorAll("[role=tab]")) {
      const selected = tab.dataset.panel === panel;
      tab.setAttribute("aria-selected", String(selected));
      tab.tabIndex = selected ? 0 : -1;
    }
    for (const section of card.querySelectorAll("[data-panel-content]"))
      section.hidden = section.dataset.panelContent !== panel;
  };
  setPanel(activePanel);
  detailTablist.addEventListener("click", event => {
    const tab = event.target.closest("[role=tab]");
    if (tab) setPanel(tab.dataset.panel);
  });
  detailTablist.addEventListener("keydown", event => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    const tabs = [...detailTablist.querySelectorAll("[role=tab]")];
    const current = tabs.indexOf(document.activeElement);
    if (current < 0) return;
    event.preventDefault();
    const step = event.key === "ArrowRight" ? 1 : -1;
    const next = tabs[(current + step + tabs.length) % tabs.length];
    setPanel(next.dataset.panel);
    next.focus();
  });
  // live mode toggle
  card.addEventListener("input", () => {
    const draft = {};
    for (let n=1;n<=7;n++) draft[String(n)] = {
      limit: card.querySelector(`input[data-day="${n}"][data-f="limit"]`).value,
      windows: card.querySelector(`input[data-day="${n}"][data-f="windows"]`).value,
    };
    drafts.set(d.device_id, draft);
  });
  card.querySelectorAll('input[data-f="windows"]').forEach(inp=>{
    inp.addEventListener("input", ()=>{
      const day = inp.dataset.day;
      const lim = card.querySelector(`input[data-day="${day}"][data-f="limit"]`);
       const mode = card.querySelector(`.mode-value[data-day="${day}"]`);
      const has = inp.value.trim().length>0;
      mode.textContent = has?"WINDOW":"LIMIT";
      lim.disabled = has;
    });
    inp.dispatchEvent(new Event("input"));
  });
  card.querySelectorAll('[data-act="daily-bonus"]').forEach(button=>{
    button.onclick = async ()=>{
      try {
        await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}/daily-bonus`, {
          method:"POST", headers:{"content-type":"application/json"}, body:JSON.stringify({minutes:Number(button.dataset.minutes)})
        });
        await refresh(true, document.activeElement === button
          ? {deviceId: d.device_id, minutes: button.dataset.minutes}
          : null);
      } catch(e) { alert("Add time failed: " + e.message); }
    };
  });
  const lockButton = card.querySelector('[data-act="lock"]');
  lockButton.onclick = async ()=>{
    const currentDevice = devices.find(device => device.device_id === d.device_id) ?? d;
    const forceLock = !currentDevice.force_lock;
    lockButton.disabled = true;
    try {
      const updated = await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}`, {
        method:"PUT", headers:{"content-type":"application/json"}, body: JSON.stringify({force_lock: forceLock})
      });
      currentDevice.force_lock = updated.config.force_lock;
      d.force_lock = updated.config.force_lock;
      const label = d.force_lock ? "Unlock device" : "Lock device now";
      lockButton.innerHTML = d.force_lock ? ICONS.unlock : ICONS.lock;
      lockButton.setAttribute("aria-label", label);
      lockButton.title = label;
      renderTabs();
      await refresh();
    } catch(e) { alert("Lock update failed: " + e.message); }
    finally { if (lockButton.isConnected) lockButton.disabled = false; }
  };
  card.querySelector('[data-act="message"]').onclick = async ()=>{
    const text = prompt(`Message to your child (up to 1000 characters; valid for ${formatMinutes(15)}):`);
    if (text === null || !text.trim()) return;
    if (text.length > 1000) { alert("Maximum 1000 characters."); return; }
    try {
      await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}/messages`, {
        method:"POST", headers:{"content-type":"application/json"}, body:JSON.stringify({text})
      });
      await refresh();
    } catch(e) { alert("Send failed: " + e.message); }
  };
  const renameButton = card.querySelector('[data-act="rename"]');
  renameButton.onclick = async ()=>{
    const name = prompt(`Rename device "${d.name}" (up to 80 characters):`, d.name);
    if (name === null) return;
    const trimmedName = name.trim();
    if (!trimmedName) { alert("Device name cannot be empty."); return; }
    if (trimmedName.length > 80) { alert("Device name must be 80 characters or fewer."); return; }
    renameButton.disabled = true;
    try {
      const updated = await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}/name`, {
        method:"PUT", headers:{"content-type":"application/json"}, body:JSON.stringify({name:trimmedName})
      });
      const currentDevice = devices.find(device => device.device_id === d.device_id) ?? d;
      currentDevice.name = updated.name;
      d.name = updated.name;
      renderTabs();
    } catch(e) { alert("Rename failed: " + e.message); }
    finally { if (renameButton.isConnected) renameButton.disabled = false; }
  };
  card.querySelector('[data-act="del"]').onclick = async ()=>{
    if (!confirm(`Delete device "${d.name}" (${d.device_id})?\n\nThis removes its config, limits and history. The client on that PC will fail authentication until you reinstall it.`)) return;
    try {
      await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}`, {method:"DELETE"});
    } catch(e){ alert("Delete failed: "+e.message); return; }
    if (selectedId() === d.device_id) {
      localStorage.removeItem("toomuch-selected");
      history.replaceState(null, "", location.pathname + location.search);
    }
    refresh();
  };
  card.querySelector('[data-act="save"]').onclick = async ()=>{
    try {
    ++refreshSequence;
    const days = {};
    for (let n=1;n<=7;n++) {
      const lim = Number(card.querySelector(`input[data-day="${n}"][data-f="limit"]`).value);
      const wraw = card.querySelector(`input[data-day="${n}"][data-f="windows"]`).value.trim();
      const windows = parseWindows(wraw);
      days[String(n)] = {limit_min: lim, windows};
    }
    await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}`, {method:"PUT", headers:{"content-type":"application/json"}, body: JSON.stringify({days})});
    drafts.delete(d.device_id);
    await refresh(true);
    } catch(e) { document.getElementById("status").textContent = "Save failed: " + e.message; }
  };
  return card;
}

function barWidth(today) {
  return usagePercent(today);
}
function esc(s){ return String(s??"").replace(/[&<>"]/g, c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c])); }

window.addEventListener("hashchange", ()=>{ renderTabs(); renderDetail(); });
// installer command uses this server's actual origin, not a hardcoded hostname
document.getElementById("installer-cmd").textContent = `.\\install.ps1 -ServerUrl ${location.origin}`;
document.querySelectorAll('[data-act="copy-command"]').forEach(button => {
  button.innerHTML = ICONS.copy;
  button.onclick = async () => {
    const command = button.closest(".command-row")?.querySelector("[data-copy-source]")?.textContent?.trim();
    if (!command) return;
    try {
      await copyTextToClipboard(command);
      button.innerHTML = ICONS.check;
      button.title = "Copied";
      button.setAttribute("aria-label", "Copied to clipboard");
      setTimeout(() => {
        if (!button.isConnected) return;
        button.innerHTML = ICONS.copy;
        button.title = "Copy command";
        button.setAttribute("aria-label", "Copy command");
      }, 1500);
    } catch {
      alert("Could not copy command. Please copy it manually.");
    }
  };
});
const modal = document.getElementById("installer-modal");
document.getElementById("open-installer").onclick = ()=>{ modal.hidden = false; loadVersion(); };
document.getElementById("close-installer").onclick = ()=>{ modal.hidden = true; };
modal.addEventListener("click", (e)=>{ if (e.target === modal) modal.hidden = true; });
document.addEventListener("keydown", (e)=>{ if (e.key === "Escape") modal.hidden = true; });
document.getElementById("refresh").onclick = refresh;
document.getElementById("dl").onclick = ()=>{ window.location.href="/api/admin/client-download"; };

if ("serviceWorker" in navigator) {
  navigator.serviceWorker.register("/service-worker.js").catch(error => console.warn("PWA offline shell registration failed:", error));
}

loadVersion(); refresh(); setInterval(refresh, 30000);
