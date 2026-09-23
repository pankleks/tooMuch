const DAY_NAMES = {1:"Pn",2:"Wt",3:"Śr",4:"Cz",5:"Pt",6:"So",7:"Nd"};

async function api(path, opts={}) {
  const r = await fetch(path, opts);
  if (r.status === 401) { document.getElementById("status").textContent = "401 – podaj hasło admin (BasicAuth)"; throw new Error("auth"); }
  const t = await r.text();
  let j = null; try { j = t ? JSON.parse(t) : null; } catch { j = {raw:t}; }
  if (!r.ok) throw new Error((j && j.error) || r.statusText);
  return j;
}

async function loadVersion() {
  try {
    const v = await api("/api/client-version");
    document.getElementById("ver").textContent = `wersja ${v.version} sha ${String(v.sha256||"").slice(0,12)}`;
  } catch { document.getElementById("ver").textContent = "brak paczki w /data/dist"; }
}

function pct(used, quotaStr, mode) {
  if (mode !== "limit" || !quotaStr) return "";
  const lim = parseInt(quotaStr);
  if (!lim) return used > 0 ? "BLOCK" : "0%";
  return Math.min(100, Math.round(used/lim*100)) + "%";
}

async function refresh() {
  const st = document.getElementById("status");
  const list = document.getElementById("list");
  st.textContent = "ładowanie…"; list.innerHTML = "";
  try {
    const ov = await api("/api/admin/overview");
    st.textContent = `urządzeń: ${ov.devices.length}`;
    for (const d of ov.devices) {
      const today = d.today;
      const card = document.createElement("div");
      card.className = "card";
      const lockBadge = d.force_lock ? `<span class="badge lock">LOCK</span>` : `<span class="badge">OK</span>`;
      card.innerHTML = `
        <div class="row"><strong>${esc(d.name)}</strong> <span class="muted">${esc(d.device_id)} v${d.version}</span> ${lockBadge}
        <span class="muted">seen: ${esc(d.last_seen||"never")}</span></div>
        <div class="row"><span>Dziś: <strong>${today?today.active_min:0} min</strong> / ${esc(today?today.quota:"?")} (${esc(today?today.mode:"?")}) ${esc(pct(today?today.active_min:0, today?today.quota:"", today?today.mode:""))}</span>
        <div class="bar" style="flex:1"><i style="width:${barWidth(today)}%"></i></div></div>
        <div class="row">
          <button data-act="lock">${d.force_lock?"Odblokuj":"Zablokuj teraz"}</button>
          <button data-act="save">Zapisz dni</button>
          <span class="muted">Okna zastępują limit. Format okien: 16:00-20:00, wiele po przecinku. 0 = blokada.</span>
        </div>
        <table><tr><th>Dzień</th><th>Limit [min]</th><th>Okna</th><th>Tryb</th></tr>
        ${[1,2,3,4,5,6,7].map(n=>{
          const c = d.config.days[String(n)];
          return `<tr><td>${DAY_NAMES[n]}</td>
          <td><input type="number" min="0" max="1440" data-day="${n}" data-f="limit" value="${c.limit_min}"></td>
          <td><input type="text" data-day="${n}" data-f="windows" value="${esc((c.windows||[]).map(w=>w.from+"-"+w.to).join(", "))}" placeholder="pusto = tryb limitu"></td>
          <td class="mode" data-day="${n}">${(c.windows&&c.windows.length)?"OKNO":"LIMIT"}</td></tr>`;
        }).join("")}
        </table>
        <div class="muted">Ostatnie 7 dni:</div>
        <table><tr><th>Data</th><th>Tryb</th><th>Quota</th><th>Zużyto</th></tr>
        ${d.last7.map(x=>`<tr><td>${x.date}</td><td>${esc(x.mode??"-")}</td><td>${esc(x.quota??"-")}</td><td>${x.active_min} min</td></tr>`).join("")}
        </table>`;
      // live mode toggle
      card.querySelectorAll('input[data-f="windows"]').forEach(inp=>{
        inp.addEventListener("input", ()=>{
          const day = inp.dataset.day;
          const lim = card.querySelector(`input[data-day="${day}"][data-f="limit"]`);
          const mode = card.querySelector(`.mode[data-day="${day}"]`);
          const has = inp.value.trim().length>0;
          mode.textContent = has?"OKNO":"LIMIT";
          lim.disabled = has;
        });
        inp.dispatchEvent(new Event("input"));
      });
      card.querySelector('[data-act="lock"]').onclick = async ()=>{
        await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}`, {method:"PUT", headers:{"content-type":"application/json"}, body: JSON.stringify({force_lock: !d.force_lock})});
        refresh();
      };
      card.querySelector('[data-act="save"]').onclick = async ()=>{
        const days = {};
        for (let n=1;n<=7;n++) {
          const lim = Number(card.querySelector(`input[data-day="${n}"][data-f="limit"]`).value);
          const wraw = card.querySelector(`input[data-day="${n}"][data-f="windows"]`).value.trim();
          let windows = [];
          if (wraw) {
            for (const part of wraw.split(",")) {
              const [from,to] = part.trim().split("-").map(s=>s.trim());
              if (!from || !to) throw alert("zły format okien");
              windows.push({from,to});
            }
          }
          days[String(n)] = {limit_min: lim, windows};
        }
        await api(`/api/admin/devices/${encodeURIComponent(d.device_id)}`, {method:"PUT", headers:{"content-type":"application/json"}, body: JSON.stringify({days})});
        refresh();
      };
      list.appendChild(card);
    }
  } catch(e){ st.textContent = "błąd: "+e.message; }
}

function barWidth(today) {
  if (!today || today.mode!=="limit") return today?50:0;
  const lim = parseInt(today.quota); if(!lim) return today.active_min>0?100:0;
  return Math.min(100, Math.round(today.active_min/lim*100));
}
function esc(s){ return String(s??"").replace(/[&<>"]/g, c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c])); }

document.getElementById("refresh").onclick = refresh;
document.getElementById("dl").onclick = ()=>{ window.location.href="/api/admin/client-download"; };
loadVersion(); refresh(); setInterval(refresh, 30000);
