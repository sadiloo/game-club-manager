let selectedSessionId = null;
let selectedTile = null;
let currentRole = "Operator";
let paymentDraft = [];
let destinationCards = [];

function showError(msg) {
  const el = document.getElementById("error");
  el.textContent = msg || "";
}

function fmtTehran(iso) {
  if (!iso) return "—";
  return new Date(iso).toLocaleString("en-GB", {
    timeZone: "Asia/Tehran",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  });
}

function cashTendered() {
  return paymentDraft.filter((l) => l.method === "Cash").reduce((s, l) => s + l.amountToman, 0);
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
    await refreshPendingC2c();
    setInterval(async () => {
      await refreshGrid();
      await refreshPendingC2c();
    }, 10000);
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

async function refreshPendingC2c() {
  const list = await api("/api/operator/payments/pending-card-to-card");
  const ul = document.getElementById("pendingC2cList");
  ul.replaceChildren();
  for (const row of list || []) {
    const li = document.createElement("li");
    li.textContent = `#${row.invoiceNumber} — ${row.amountToman} toman`;
    if (row.destinationCardId) li.textContent += ` → ${row.destinationCardId}`;
    const confirmBtn = document.createElement("button");
    confirmBtn.type = "button";
    confirmBtn.textContent = "Confirm transfer";
    const rejectBtn = document.createElement("button");
    rejectBtn.type = "button";
    rejectBtn.textContent = "Reject";
    confirmBtn.onclick = async () => {
      if (confirmBtn.disabled) return;
      confirmBtn.disabled = true;
      rejectBtn.disabled = true;
      try {
        await api(`/api/operator/payments/${row.id}/confirm`, { method: "POST" });
        await refreshPendingC2c();
      } catch (_) {
        confirmBtn.disabled = false;
        rejectBtn.disabled = false;
      }
    };
    rejectBtn.onclick = async () => {
      if (rejectBtn.disabled) return;
      confirmBtn.disabled = true;
      rejectBtn.disabled = true;
      try {
        await api(`/api/operator/payments/${row.id}/reject`, { method: "POST" });
        await refreshPendingC2c();
      } catch (_) {
        confirmBtn.disabled = false;
        rejectBtn.disabled = false;
      }
    };
    li.appendChild(confirmBtn);
    li.appendChild(rejectBtn);
    ul.appendChild(li);
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
    const endInput = document.createElement("input");
    endInput.type = "datetime-local";
    endInput.dataset.sessionId = row.sessionId;
    const btnB = document.createElement("button");
    btnB.type = "button";
    btnB.textContent = "Bill to end time";
    btnB.onclick = async () => {
      const raw = endInput.value;
      if (!raw) {
        showError("Enter an end time.");
        return;
      }
      const endAt = `${raw}:00+03:30`;
      await api(`/api/operator/sessions/${row.sessionId}/manual-settle`, {
        method: "POST",
        body: JSON.stringify({ endAt }),
      });
      await openBillScreen(row.sessionId);
      await refreshManualSettle();
      await refreshGrid();
    };
    li.appendChild(btnA);
    li.appendChild(endInput);
    li.appendChild(btnB);
    ul.appendChild(li);
  }
}

function renderBill(bill) {
  document.getElementById("bill").classList.remove("hidden");
  const d = document.getElementById("billDetail");
  const segText = (bill.segments || [])
    .map(
      (s) =>
        `${s.segmentKey}: ${s.playerCount}p ${s.mode} @ ${s.hourlyToman ?? "?"} toman/h ` +
        `${fmtTehran(s.startUtc)}–${fmtTehran(s.endUtc)}`,
    )
    .join("\n");
  d.textContent =
    `Group ${bill.groupId || ""} · players ${bill.playerCount ?? "?"}\n` +
    `Unrounded ${bill.unroundedPayableToman} + rounding ${bill.roundingToman} = collect ${bill.collectibleToman}` +
    (bill.surchargeToman ? ` (surcharge ${bill.surchargeToman})` : "") +
    (segText ? `\nSegments:\n${segText}` : "");
  const changeEl = document.getElementById("cashChange");
  if (changeEl) {
    const cash = cashTendered();
    const collect = bill.collectibleToman ?? 0;
    changeEl.textContent =
      collect > 0 && cash > 0 ? `Cash tendered ${cash}; change ${Math.max(0, cash - collect)} toman` : "";
  }
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
      api(`/api/operator/sessions/${selectedSessionId}/bill`)
        .then(renderBill)
        .catch(() => {});
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
  api(`/api/operator/sessions/${selectedSessionId}/bill`)
    .then(renderBill)
    .catch(() => {});
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
  if (bill.collectibleToman === 0) {
    payments = [];
  } else if (payments.length === 0) {
    payments = [{ method: "Cash", amountToman: bill.collectibleToman }];
  }
  await api(`/api/operator/sessions/${selectedSessionId}/bill/confirm`, {
    method: "POST",
    body: JSON.stringify({ payments }),
  });
  selectedSessionId = null;
  document.getElementById("bill").classList.add("hidden");
  await refreshGrid();
  await refreshPendingC2c();
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
