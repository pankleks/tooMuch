import { formatMinutes, formatQuota, formatTimeAgo } from "./format.js";
import { deviceStatus } from "./device-status.js";

const DAY_NAMES = {1:"Mon",2:"Tue",3:"Wed",4:"Thu",5:"Fri",6:"Sat",7:"Sun"};
// Lucide icons (ISC-licensed), embedded locally so the admin PWA works offline.
const ICONS = {
  lock: '<svg aria-hidden="true" viewBox="0 0 24 24"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>',
  unlock: '<svg aria-hidden="true" viewBox="0 0 24 24"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/></svg>',
  message: '<svg aria-hidden="true" viewBox="0 0 24 24"><path d="M21 15a4 4 0 0 1-4 4H7l-4 4V7a4 4 0 0 1 4-4h10a4 4 0 0 1 4 4z"/></svg>',
  trash: '<svg aria-hidden="true" viewBox="0 0 24 24"><path d="M3 6h18"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><path d="M10 11v6"/><path d="M14 11v6"/></svg>'
};
let devices = [];
const drafts = new Map();
const detailPanels = new Map();
let refreshSequence = 0;

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

function pct(used, quotaStr, mode) {
  if (mode !== "limit" || !quotaStr) return "";
  const lim = parseInt(quotaStr);
  if (!lim) return used > 0 ? "BLOCK" : "0%";
  return Math.min(100, Math.round(used/lim*100)) + "%";
}

function todaySummary(d) {
  const t = d.today;
  if (!t) return "no data";
  return `${formatMinutes(t.active_min)} / ${formatQuota(t.quota)}`;
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
    b.className = "tab" + (d.device_id===sel ? " active" : "");
    const status = deviceStatus(d);
    b.innerHTML = `<span class="dot ${status.dot}"></span><strong>${esc(d.name)}</strong><span class="tmin">${esc(todaySummary(d))}</span>`;
    b.title = `${d.device_id} — ${status.label}${d.force_lock ? "\nParent lock is enabled" : ""}`;
    b.onclick = ()=>select(d.device_id);
    tabs.appendChild(b);
  }
}

function renderDetail() {
  const list = document.getElementById("list");
  list.innerHTML = "";
  const d = devices.find(x=>x.device_id===selectedId());
  if (!d) { list.innerHTML = `<p class="muted">No devices yet. Install the client on a PC – it will auto-register here.</p>`; return; }
  list.appendChild(buildCard(d));
}

async function refresh() {
  const sequence = ++refreshSequence;
  const st = document.getElementById("status");
  st.textContent = "loading…";
  try {
    const ov = await api("/api/admin/overview");
    if (sequence !== refreshSequence) return;
    devices = ov.devices || [];
    st.textContent = `devices: ${devices.length}`;
    renderTabs();
    if (!document.querySelector("#list :focus")) renderDetail();
  } catch(e){
    st.textContent = navigator.onLine ? "server unavailable: " + e.message : "offline — reconnect to the tooMuch server";
    if (!devices.length) document.getElementById("list").innerHTML = `<p class="muted">The admin panel is available offline, but device data and changes require a connection to the server.</p>`;
  }
}

function buildCard(d) {
  const today = d.today;
  const card = document.createElement("div");
  card.className = "card";
  const lockBadge = d.locked === true ? `<span class="badge lock">LOCK</span>` : `<span class="badge">${d.locked === false ? "OK" : "UNKNOWN"}</span>`;
  const activePanel = detailPanels.get(d.device_id) ?? "history";
  const lockLabel = d.force_lock ? "Unlock device" : "Lock device now";
  card.innerHTML = `
    <div class="row"><strong>${esc(d.name)}</strong> <span class="muted">${esc(d.device_id)} v${d.version}</span> ${lockBadge}
    <span class="muted">seen: ${esc(formatTimeAgo(d.last_seen))}</span></div>
    <div class="row"><span>Today: <strong>${formatMinutes(today?today.active_min:0)}</strong> / ${esc(formatQuota(today?today.quota:"?"))} (${esc(today?today.mode:"?")}) ${esc(pct(today?today.active_min:0, today?today.quota:"", today?today.mode:""))}</span>
    <div class="bar" style="flex:1"><i style="width:${barWidth(today)}%"></i></div></div>
    <div class="row action-row">
      <button type="button" class="icon-button" data-act="lock" aria-label="${lockLabel}" title="${lockLabel}">${d.force_lock?ICONS.unlock:ICONS.lock}</button>
      <button type="button" class="icon-button" data-act="message" aria-label="Send message to child" title="Send message to child">${ICONS.message}</button>
      <button type="button" class="icon-button danger" data-act="del" aria-label="Delete device" title="Delete device">${ICONS.trash}</button>
    </div>
    <div class="muted recent-messages">${(d.messages||[]).slice(-5).reverse().map(m=>`${esc(m.text)} — ${esc({pending:"Pending",delivered:"Delivered to client",confirmed:"Confirmed (OK)",expired:"Expired"}[m.status]||m.status)}`).join("<br>")}</div>
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
      <button data-act="save">Save days</button>
      <span class="muted action-help">Windows override the limit. Use 16:00-20:00, comma-separated for multiple. 0 = blocked.</span>
    </div>
    </section>`;
  detailPanels.set(d.device_id, activePanel);
  const detailTablist = card.querySelector(".detail-tabs");
  const setPanel = panel => {
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
  card.querySelector('[data-act="lock"]').onclick = async ()=>{
    await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}`, {method:"PUT", headers:{"content-type":"application/json"}, body: JSON.stringify({force_lock: !d.force_lock})});
    refresh();
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
      let windows = [];
      if (wraw) {
        for (const part of wraw.split(",")) {
          const [from,to] = part.trim().split("-").map(s=>s.trim());
          if (!from || !to) throw alert("bad window format");
          windows.push({from,to});
        }
      }
      days[String(n)] = {limit_min: lim, windows};
    }
    await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}`, {method:"PUT", headers:{"content-type":"application/json"}, body: JSON.stringify({days})});
    drafts.delete(d.device_id);
    refresh();
    } catch(e) { document.getElementById("status").textContent = "Save failed: " + e.message; }
  };
  return card;
}

function barWidth(today) {
  if (!today || today.mode!=="limit") return today?50:0;
  const lim = parseInt(today.quota); if(!lim) return today.active_min>0?100:0;
  return Math.min(100, Math.round(today.active_min/lim*100));
}
function esc(s){ return String(s??"").replace(/[&<>"]/g, c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c])); }

window.addEventListener("hashchange", ()=>{ renderTabs(); renderDetail(); });
// installer command uses this server's actual origin, not a hardcoded hostname
document.getElementById("installer-cmd").textContent = `.\\install.ps1 -ServerUrl ${location.origin}`;
const modal = document.getElementById("installer-modal");
document.getElementById("open-installer").onclick = ()=>{ modal.hidden = false; loadVersion(); };
document.getElementById("close-installer").onclick = ()=>{ modal.hidden = true; };
modal.addEventListener("click", (e)=>{ if (e.target === modal) modal.hidden = true; });
document.addEventListener("keydown", (e)=>{ if (e.key === "Escape") modal.hidden = true; });
document.getElementById("refresh").onclick = refresh;
document.getElementById("dl").onclick = ()=>{ window.location.href="/api/admin/client-download"; };

const installButton = document.getElementById("install-app");
let installPrompt = null;
const secureContext = window.isSecureContext;
const isStandalone = window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
const isAppleMobile = /iPhone|iPad|iPod/i.test(navigator.userAgent)
  || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
if (!secureContext || (isAppleMobile && !isStandalone)) installButton.hidden = false;

window.addEventListener("beforeinstallprompt", event => {
  event.preventDefault();
  installPrompt = event;
  installButton.hidden = false;
});
installButton.addEventListener("click", async () => {
  if (!secureContext) {
    alert("PWA installation requires HTTPS. Open this panel from a trusted HTTPS address first.");
  } else if (installPrompt) {
    installPrompt.prompt();
    await installPrompt.userChoice;
    installPrompt = null;
    installButton.hidden = true;
  } else if (isAppleMobile && !isStandalone) {
    alert("To install tooMuch, tap Share in Safari and choose Add to Home Screen.");
  }
});
window.addEventListener("appinstalled", () => {
  installButton.hidden = true;
  installPrompt = null;
});

if ("serviceWorker" in navigator) {
  navigator.serviceWorker.register("/service-worker.js").catch(error => console.warn("PWA offline shell registration failed:", error));
}

loadVersion(); refresh(); setInterval(refresh, 30000);
