import { formatMinutes, formatQuota, formatTimeAgo } from "./format.js";

const DAY_NAMES = {1:"Mon",2:"Tue",3:"Wed",4:"Thu",5:"Fri",6:"Sat",7:"Sun"};
let devices = [];
const drafts = new Map();
let refreshSequence = 0;
const STALE_MS = 5 * 60 * 1000;

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

function dotClass(d) {
  if (d.force_lock) return "lock";
  if (!d.last_seen || (Date.now() - Date.parse(d.last_seen)) > STALE_MS) return "stale";
  return "ok";
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
    b.innerHTML = `<span class="dot ${dotClass(d)}"></span><strong>${esc(d.name)}</strong><span class="tmin">${esc(todaySummary(d))}</span>`;
    b.title = d.device_id;
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
    if (!document.querySelector('#list input:focus')) renderDetail();
  } catch(e){ st.textContent = "error: "+e.message; }
}

function buildCard(d) {
  const today = d.today;
  const card = document.createElement("div");
  card.className = "card";
  const lockBadge = d.locked === true ? `<span class="badge lock">LOCK</span>` : `<span class="badge">${d.locked === false ? "OK" : "UNKNOWN"}</span>`;
  card.innerHTML = `
    <div class="row"><strong>${esc(d.name)}</strong> <span class="muted">${esc(d.device_id)} v${d.version}</span> ${lockBadge}
    <span class="muted">seen: ${esc(formatTimeAgo(d.last_seen))}</span></div>
    <div class="row"><span>Today: <strong>${formatMinutes(today?today.active_min:0)}</strong> / ${esc(formatQuota(today?today.quota:"?"))} (${esc(today?today.mode:"?")}) ${esc(pct(today?today.active_min:0, today?today.quota:"", today?today.mode:""))}</span>
    <div class="bar" style="flex:1"><i style="width:${barWidth(today)}%"></i></div></div>
    <div class="row">
      <button data-act="lock">${d.force_lock?"Unlock":"Lock now"}</button>
      <button data-act="message">Send message</button>
      <span class="muted">Windows override the limit. Window format: 16:00-20:00, comma-separated for multiple. 0 = blocked.</span>
      <button data-act="del" class="danger" style="margin-left:auto">Delete</button>
    </div>
    <div class="muted">${(d.messages||[]).slice(-5).reverse().map(m=>`${esc(m.text)} — ${esc({pending:"Pending",delivered:"Delivered to client",confirmed:"Confirmed (OK)",expired:"Expired"}[m.status]||m.status)}`).join("<br>")}</div>
    <table><tr><th>Day</th><th>Limit [min]</th><th>Windows</th><th>Mode</th></tr>
    ${[1,2,3,4,5,6,7].map(n=>{
      const c = d.config.days[String(n)];
      const draft = drafts.get(d.device_id)?.[String(n)];
      return `<tr><td>${DAY_NAMES[n]}</td>
      <td><input type="number" min="0" max="1440" data-day="${n}" data-f="limit" value="${esc(draft?.limit ?? c.limit_min)}"></td>
      <td><input type="text" data-day="${n}" data-f="windows" value="${esc(draft?.windows ?? (c.windows||[]).map(w=>w.from+"-"+w.to).join(", "))}" placeholder="empty = limit mode"></td>
      <td class="mode" data-day="${n}">${(c.windows&&c.windows.length)?"WINDOW":"LIMIT"}</td></tr>`;
    }).join("")}
    </table>
    <div class="row">
      <button data-act="save">Save days</button>
    </div>
    <table><tr><th>Date</th><th>Mode</th><th>Quota</th><th>Used</th></tr>
    ${d.last7.map(x=>`<tr><td>${x.date}</td><td>${esc(x.mode??"-")}</td><td>${esc(formatQuota(x.quota??"-"))}</td><td>${formatMinutes(x.active_min)}</td></tr>`).join("")}
    </table>`;
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
      const mode = card.querySelector(`.mode[data-day="${day}"]`);
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
loadVersion(); refresh(); setInterval(refresh, 30000);
