let selectedSessionId = null;
let selectedTile = null;
let currentRole = "Operator";
let paymentDraft = [];
let destinationCards = [];

function showError(msg) {
  const el = document.getElementById("error");
  el.textContent = msg || "";
}

async function api(path, options = {}) {
  showError("");
  const res = await fetch(path, {
    credentials: "include",
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options,
  });
  const text = await res.text();
  if (!res.ok) {
    let msg = text;
    try {
      const j = JSON.parse(text);
      msg = j.message || j.code || text;
    } catch (_) {}
    showError(msg);
    throw new Error(msg);
  }
  if (res.status === 204 || !text) return null;
  return JSON.parse(text);
}

document.getElementById("loginBtn").onclick = async () => {
  try {
    const user = await api("/api/operator/auth/login", {
      method: "POST",
      body: JSON.stringify({
        username: document.getElementById("username").value,
        pin: document.getElementById("pin").value,
      }),
    });
    currentRole = user.role;
    document.getElementById("login").classList.add("hidden");
    document.getElementById("app").classList.remove("hidden");
    destinationCards = (await api("/api/operator/destination-cards")) || [];
    await refreshGrid();
    await refreshManualSettle();
    setInterval(refreshGrid, 10000);
  } catch (e) {
    document.getElementById("loginError").textContent = String(e.message);
    document.getElementById("loginError").classList.remove("hidden");
  }
};

async function refreshGrid() {
  const tiles = await api("/api/operator/grid");
  const grid = document.getElementById("grid");
  grid.replaceChildren();
  for (const t of tiles) {
    const el = document.createElement("div");
    el.className = "tile";
    el.dataset.status = t.status;
    el.dataset.stationId = t.id;
    const title = document.createElement("strong");
    title.textContent = t.id;
    el.appendChild(title);
    el.appendChild(document.createElement("br"));
    el.appendChild(document.createTextNode(t.status));
    if (t.amountToman != null) {
      el.appendChild(document.createElement("br"));
      el.appendChild(document.createTextNode(String(t.amountToman)));
    }
    el.onclick = () => onTileClick(t);
    grid.appendChild(el);
  }
}

async function refreshManualSettle() {
  const list = await api("/api/operator/manual-settle-queue");
  const ul = document.getElementById("manualSettleList");
  ul.replaceChildren();
  for (const row of list) {
    const li = document.createElement("li");
    li.textContent = `${row.stationId} — ${row.reason || "manual"}`;
    const btnA = document.createElement("button");
    btnA.type = "button";
    btnA.textContent = "Bill to checkpoint";
    btnA.onclick = async () => {
      await api(`/api/operator/sessions/${row.sessionId}/manual-settle`, { method: "POST", body: "{}" });
      await openBillScreen(row.sessionId);
      await refreshManualSettle();
      await refreshGrid();
    };
    li.appendChild(btnA);
    ul.appendChild(li);
  }
}

function renderBill(bill) {
  document.getElementById("bill").classList.remove("hidden");
  const d = document.getElementById("billDetail");
  const segText = (bill.segments || [])
    .map((s) => `${s.segmentKey}: ${s.playerCount}p ${s.mode}`)
    .join("; ");
  d.textContent =
    `Group ${bill.groupId || ""} · players ${bill.playerCount ?? "?"}\n` +
    `Unrounded ${bill.unroundedPayableToman} + rounding ${bill.roundingToman} = collect ${bill.collectibleToman}` +
    (bill.surchargeToman ? ` (surcharge ${bill.surchargeToman})` : "") +
    (segText ? `\nSegments: ${segText}` : "");
  document.getElementById("billAmount")?.remove();
}

function renderPaymentLines() {
  const box = document.getElementById("paymentLines");
  box.replaceChildren();
  paymentDraft.forEach((line, idx) => {
    const row = document.createElement("div");
    row.className = "pay-line";
    row.textContent = `${line.method} ${line.amountToman}`;
    if (line.destinationCardId) row.textContent += ` → ${line.destinationCardId}`;
    const del = document.createElement("button");
    del.type = "button";
    del.textContent = "Remove";
    del.onclick = () => {
      paymentDraft.splice(idx, 1);
      renderPaymentLines();
    };
    row.appendChild(del);
    box.appendChild(row);
  });
}

async function openBillScreen(sessionId) {
  selectedSessionId = sessionId;
  paymentDraft = [];
  const bill = await api(`/api/operator/sessions/${sessionId}/bill`);
  renderBill(bill);
  renderPaymentLines();
}

async function onTileClick(t) {
  selectedTile = t;
  document.getElementById("sessionActions").classList.add("hidden");
  if (t.status === "Free") {
    await api(`/api/operator/stations/${encodeURIComponent(t.id)}/start`, { method: "POST" });
    await refreshGrid();
    return;
  }
  if (!t.sessionId) return;
  selectedSessionId = t.sessionId;
  if (t.status === "Waiting for payment") {
    await openBillScreen(t.sessionId);
    return;
  }
  if (t.status === "Manual settle") {
    await refreshManualSettle();
    return;
  }
  document.getElementById("sessionActions").classList.remove("hidden");
  document.getElementById("sessionLabel").textContent = `${t.id} — ${t.status}`;
  document.getElementById("pauseBtn").classList.toggle("hidden", t.status !== "Playing");
  document.getElementById("resumeBtn").classList.toggle("hidden", t.status !== "Paused");
  document.getElementById("openBillBtn").classList.toggle("hidden", (t.groupId || "").startsWith("pc"));
}

document.getElementById("pauseBtn").onclick = async () => {
  await api(`/api/operator/sessions/${selectedSessionId}/pause`, { method: "POST" });
  await refreshGrid();
};

document.getElementById("resumeBtn").onclick = async () => {
  await api(`/api/operator/sessions/${selectedSessionId}/resume`, { method: "POST" });
  await refreshGrid();
};

document.getElementById("playerCountBtn").onclick = async () => {
  const playerCount = Number(document.getElementById("playerCountSelect").value);
  await api(`/api/operator/sessions/${selectedSessionId}/player-count`, {
    method: "PATCH",
    body: JSON.stringify({ playerCount }),
  });
  await refreshGrid();
};

document.getElementById("openBillBtn").onclick = async () => {
  const bill = await api(`/api/operator/sessions/${selectedSessionId}/bill/open`, { method: "POST" });
  renderBill(bill);
  paymentDraft = [];
  renderPaymentLines();
  await refreshGrid();
};

function promptAmount(label) {
  const v = window.prompt(label, "0");
  if (v == null) return null;
  return Number(v.replace(/,/g, ""));
}

document.getElementById("addCashLineBtn").onclick = () => {
  const amountToman = promptAmount("Cash amount (toman)");
  if (amountToman == null || Number.isNaN(amountToman)) return;
  paymentDraft.push({ method: "Cash", amountToman });
  renderPaymentLines();
};

document.getElementById("addCardLineBtn").onclick = () => {
  const amountToman = promptAmount("Card reader amount (toman)");
  if (amountToman == null || Number.isNaN(amountToman)) return;
  paymentDraft.push({ method: "CardReader", amountToman });
  renderPaymentLines();
};

document.getElementById("addC2cLineBtn").onclick = () => {
  const amountToman = promptAmount("Card-to-card amount (toman)");
  if (amountToman == null || Number.isNaN(amountToman)) return;
  const destinationCardId = window.prompt("Destination card id (optional)", destinationCards[0]?.id || "") || undefined;
  const reference = window.prompt("Reference / last 4 (optional)", "") || undefined;
  paymentDraft.push({ method: "CardToCard", amountToman, destinationCardId, reference });
  renderPaymentLines();
};

document.getElementById("surchargeBtn").onclick = async () => {
  const bill = await api(`/api/operator/sessions/${selectedSessionId}/bill/surcharge`, {
    method: "POST",
    body: JSON.stringify({ reason: null }),
  });
  renderBill(bill);
};

document.getElementById("confirmBtn").onclick = async () => {
  const bill = await api(`/api/operator/sessions/${selectedSessionId}/bill`);
  let payments = paymentDraft;
  if (payments.length === 0) {
    payments = [{ method: "Cash", amountToman: bill.collectibleToman }];
  }
  const result = await api(`/api/operator/sessions/${selectedSessionId}/bill/confirm`, {
    method: "POST",
    body: JSON.stringify({ payments }),
  });
  if (result && result.number) {
    const inv = await api(`/api/operator/invoices/${result.number}`);
    for (const p of inv.payments || []) {
      if (p.method === "CardToCard" && p.status === "Pending") {
        await api(`/api/operator/payments/${p.id}/confirm`, { method: "POST" });
      }
    }
  }
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

document.getElementById("loadInvoiceBtn").onclick = async () => {
  const n = document.getElementById("invoiceNumber").value;
  const inv = await api(`/api/operator/invoices/${n}`);
  document.getElementById("invoiceDetail").textContent = JSON.stringify(inv, null, 2);
};

document.getElementById("voidInvoiceBtn").onclick = async () => {
  const n = document.getElementById("invoiceNumber").value;
  const reason = window.prompt("Void reason", "") || "";
  await api(`/api/operator/invoices/${n}/void`, { method: "POST", body: JSON.stringify({ reason }) });
  document.getElementById("invoiceDetail").textContent = "Voided.";
};
