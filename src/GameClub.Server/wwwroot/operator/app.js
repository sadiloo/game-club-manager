let selectedSessionId = null;

async function api(path, options = {}) {
  const res = await fetch(path, {
    credentials: "include",
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options,
  });
  if (!res.ok) throw new Error(await res.text());
  return res.status === 204 ? null : res.json();
}

document.getElementById("loginBtn").onclick = async () => {
  await api("/api/operator/auth/login", {
    method: "POST",
    body: JSON.stringify({
      username: document.getElementById("username").value,
      pin: document.getElementById("pin").value,
    }),
  });
  document.getElementById("login").classList.add("hidden");
  document.getElementById("app").classList.remove("hidden");
  await refreshGrid();
  setInterval(refreshGrid, 10000);
};

async function refreshGrid() {
  const tiles = await api("/api/operator/grid");
  const grid = document.getElementById("grid");
  grid.innerHTML = "";
  for (const t of tiles) {
    const el = document.createElement("div");
    el.className = "tile";
    el.dataset.status = t.status;
    el.dataset.stationId = t.stationId || t.id;
    el.innerHTML = `<strong>${t.id}</strong><br/>${t.status}${t.amountToman != null ? "<br/>" + t.amountToman : ""}`;
    el.onclick = () => onTileClick(t);
    grid.appendChild(el);
  }
}

async function onTileClick(t) {
  if (t.status === "Free") {
    await api(`/api/operator/stations/${encodeURIComponent(t.id)}/start`, { method: "POST" });
    await refreshGrid();
    return;
  }
  if (t.status === "Playing" && t.sessionId) {
    selectedSessionId = t.sessionId;
    const bill = await api(`/api/operator/sessions/${selectedSessionId}/bill/open`, { method: "POST" });
    showBill(bill);
  }
}

function showBill(bill) {
  document.getElementById("bill").classList.remove("hidden");
  document.getElementById("billAmount").textContent = bill.collectibleToman;
}

document.getElementById("surchargeBtn").onclick = async () => {
  const bill = await api(`/api/operator/sessions/${selectedSessionId}/bill/surcharge`, {
    method: "POST",
    body: JSON.stringify({ reason: null }),
  });
  showBill(bill);
};

document.getElementById("confirmBtn").onclick = async () => {
  const amount = Number(document.getElementById("billAmount").textContent);
  await api(`/api/operator/sessions/${selectedSessionId}/bill/confirm`, {
    method: "POST",
    body: JSON.stringify({ payments: [{ method: "Cash", amountToman: amount }] }),
  });
  selectedSessionId = null;
  document.getElementById("bill").classList.add("hidden");
  await refreshGrid();
};

document.getElementById("cancelBillBtn").onclick = async () => {
  await api(`/api/operator/sessions/${selectedSessionId}/bill/cancel`, { method: "POST" });
  selectedSessionId = null;
  document.getElementById("bill").classList.add("hidden");
  await refreshGrid();
};
