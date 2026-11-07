let selectedSessionId = null;
let selectedTile = null;
let currentRole = "Operator";
let paymentDraft = [];
let destinationCards = [];
let catalog = [];
let buffetOpenSaleId = null;
let buffetMode = "session";
let billTarget = null;
let customerSearchTimer = null;
let displayedLinkedCustomer = null;

function resetBuffetOnlyContext() {
  buffetMode = "session";
  buffetOpenSaleId = null;
}

function isBuffetBillVisible() {
  return (
    billTarget === "buffet-only"
    && buffetOpenSaleId
    && !document.getElementById("bill").classList.contains("hidden")
  );
}

function isBuffetOnlyPanelActive() {
  return buffetMode === "buffet-only" && buffetOpenSaleId;
}

function showError(msg) {
  const el = document.getElementById("error");
  el.textContent = msg || "";
}

function fmtTehran(iso) {
  if (!iso) return "—";
  return new Date(iso).toLocaleString("fa-IR", {
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

async function loadCatalog() {
  catalog = (await api("/api/operator/catalog")) || [];
}

async function refreshLowStock() {
  const rows = (await api("/api/operator/catalog/low-stock")) || [];
  const el = document.getElementById("lowStockList");
  if (!rows.length) {
    el.textContent = "";
    return;
  }
  el.textContent = "هشدار موجودی: " + rows.map((r) => `${r.name} (${r.stockQuantity})`).join(" · ");
}

function renderBuffetCategories() {
  const box = document.getElementById("buffetCategories");
  box.replaceChildren();
  for (const cat of catalog) {
    const wrap = document.createElement("div");
    wrap.className = "cat";
    const title = document.createElement("strong");
    title.textContent = cat.name;
    wrap.appendChild(title);
    const items = document.createElement("div");
    items.className = "cat-items";
    for (const item of cat.items || []) {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "cat-item";
      btn.textContent = `${item.name} — ${item.unitPriceToman || "دلخواه"}`;
      btn.onclick = async (ev) => {
        ev.stopPropagation();
        await addCatalogLine(item);
      };
      items.appendChild(btn);
    }
    wrap.onclick = () => wrap.classList.toggle("cat-open");
    wrap.appendChild(items);
    box.appendChild(wrap);
  }
}

async function addCatalogLine(item) {
  let body = { catalogItemId: item.id };
  if (item.kind === "ServiceCustom") {
    const raw = window.prompt("مبلغ (تومان)", "20000");
    if (raw == null) return;
    const n = Number(String(raw).replace(/,/g, ""));
    if (!Number.isInteger(n) || n <= 0) {
      showError("مبلغ باید عدد صحیح مثبت باشد.");
      return;
    }
    body.customAmountToman = n;
  }
  if (buffetMode === "buffet-only") {
    await api(`/api/operator/buffet-only/${buffetOpenSaleId}/lines`, { method: "POST", body: JSON.stringify(body) });
    await renderBuffetSale();
    return;
  }
  await api(`/api/operator/sessions/${selectedSessionId}/sale/lines`, { method: "POST", body: JSON.stringify(body) });
  await renderBuffetSale();
  if (selectedSessionId && !document.getElementById("bill").classList.contains("hidden")) {
    const bill = await api(`/api/operator/sessions/${selectedSessionId}/bill`);
    renderBill(bill);
  }
}

function renderBuffetOnlyCheckoutBill(sale) {
  const b = sale?.bill;
  if (!b) {
    return;
  }
  billTarget = "buffet-only";
  renderBill({
    collectibleToman: b.collectibleToman ?? 0,
    unroundedPayableToman: b.unroundedPayableToman ?? 0,
    roundingToman: b.roundingToman ?? 0,
    timeGrossToman: b.timeGrossToman ?? 0,
    buffetGrossToman: b.buffetGrossToman ?? 0,
    servicesGrossToman: b.servicesGrossToman ?? 0,
    saleLines: sale.lines ?? [],
    segments: [],
    linkedCustomer: sale.linkedCustomer,
  });
}

async function renderBuffetSale() {
  const linesBox = document.getElementById("buffetSaleLines");
  linesBox.replaceChildren();
  let sale;
  if (buffetMode === "buffet-only") {
    sale = await api(`/api/operator/buffet-only/${buffetOpenSaleId}`);
  } else if (selectedSessionId) {
    sale = await api(`/api/operator/sessions/${selectedSessionId}/sale`);
  } else {
    return;
  }
  renderCustomerChip(sale.linkedCustomer);
  const lines = sale.lines || [];
  for (const line of lines) {
    const row = document.createElement("div");
    row.className = "sale-line";
    row.textContent = `${line.name} ×${line.quantity} = ${line.lineTotalToman}`;
    const del = document.createElement("button");
    del.type = "button";
    del.textContent = "حذف";
    del.onclick = async () => {
      if (buffetMode === "buffet-only") {
        await api(`/api/operator/buffet-only/${buffetOpenSaleId}/lines/${line.id}`, { method: "DELETE" });
      } else {
        await api(`/api/operator/sessions/${selectedSessionId}/sale/lines/${line.id}`, { method: "DELETE" });
      }
      await renderBuffetSale();
      await refreshLowStock();
      await refreshVisibleBillAfterSaleLineChange();
    };
    row.appendChild(del);
    linesBox.appendChild(row);
  }
  const checkoutBtn = document.getElementById("buffetCheckoutBtn");
  checkoutBtn.classList.toggle("hidden", buffetMode !== "buffet-only" || !lines.length);
  const billEl = document.getElementById("bill");
  if (
    buffetMode === "buffet-only"
    && billTarget === "buffet-only"
    && buffetOpenSaleId
    && sale?.bill
    && !billEl.classList.contains("hidden")
  ) {
    renderBuffetOnlyCheckoutBill(sale);
  }
}

async function openBuffetPanel(tile) {
  buffetMode = tile?.id === "buffet-only" || tile?.status === "Buffet only" ? "buffet-only" : "session";
  document.getElementById("buffetPanel").classList.remove("hidden");
  if (buffetMode === "buffet-only") {
    selectedSessionId = null;
    document.getElementById("sessionActions").classList.add("hidden");
    const start = await api("/api/operator/buffet-only/start", { method: "POST" });
    buffetOpenSaleId = start.openSaleId;
    document.getElementById("buffetContext").textContent = "فروش بوفه (بدون بازی)";
  } else {
    buffetOpenSaleId = tile?.openSaleId || null;
    selectedSessionId = tile.sessionId;
    document.getElementById("buffetContext").textContent = `ایستگاه ${tile.id} — جلسه فعال`;
  }
  await loadCatalog();
  renderBuffetCategories();
  await renderBuffetSale();
}

function customerLinkContext() {
  if (isBuffetBillVisible() || isBuffetOnlyPanelActive()) {
    return { openSaleId: buffetOpenSaleId };
  }
  if (selectedSessionId) {
    return { sessionId: selectedSessionId };
  }
  return null;
}

function renderCustomerChip(linked) {
  displayedLinkedCustomer = linked || null;
  const chip = document.getElementById("customerChip");
  const walletBox = document.getElementById("walletActions");
  if (!linked) {
    chip.classList.add("hidden");
    chip.textContent = "";
    walletBox.classList.add("hidden");
    return;
  }
  const code = linked.membershipCode ? ` · کارت ${linked.membershipCode}` : "";
  chip.textContent = `پیوسته: ${linked.name} — ${linked.mobile}${code}`;
  chip.classList.remove("hidden");
  walletBox.classList.remove("hidden");
}

document.getElementById("walletTopUpCash").onclick = async () => {
  const linked = displayedLinkedCustomer;
  if (!linked) {
    showError("عضوی پیوسته نیست.");
    return;
  }
  const raw = window.prompt("مبلغ شارژ (تومان)", "50000");
  if (!raw) return;
  const amountToman = Number(raw);
  if (!Number.isFinite(amountToman) || amountToman <= 0) {
    showError("مبلغ نامعتبر است.");
    return;
  }
  await api(`/api/operator/members/customers/${linked.id}/wallet/top-up`, {
    method: "POST",
    body: JSON.stringify({ method: "Cash", amountToman }),
  });
};

async function linkCustomer(customerId) {
  const ctx = customerLinkContext();
  if (!ctx) {
    showError("ابتدا یک جلسه یا فروش بوفه را انتخاب کنید.");
    return;
  }
  if (ctx.sessionId) {
    const linked = await api(`/api/operator/sessions/${ctx.sessionId}/customer`, {
      method: "PUT",
      body: JSON.stringify({ customerId }),
    });
    renderCustomerChip(linked);
  } else if (ctx.openSaleId) {
    const linked = await api(`/api/operator/buffet-only/${ctx.openSaleId}/customer`, {
      method: "PUT",
      body: JSON.stringify({ customerId }),
    });
    renderCustomerChip(linked);
  }
  await refreshGrid();
}

async function clearCustomerLink() {
  const ctx = customerLinkContext();
  if (!ctx) {
    return;
  }
  if (ctx.sessionId) {
    await api(`/api/operator/sessions/${ctx.sessionId}/customer`, {
      method: "PUT",
      body: JSON.stringify({ customerId: null }),
    });
  } else if (ctx.openSaleId) {
    await api(`/api/operator/buffet-only/${ctx.openSaleId}/customer`, {
      method: "PUT",
      body: JSON.stringify({ customerId: null }),
    });
  }
  renderCustomerChip(null);
  await refreshGrid();
}

async function runCustomerSearch(q) {
  const box = document.getElementById("customerSearchResults");
  box.replaceChildren();
  const trimmed = (q || "").trim();
  if (!trimmed) {
    return;
  }
  const rows = (await api(`/api/operator/customers/search?q=${encodeURIComponent(trimmed)}`)) || [];
  for (const row of rows) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "customer-hit";
    const code = row.membershipCode ? ` · ${row.membershipCode}` : "";
    btn.textContent = `${row.name} — ${row.mobile}${code}`;
    btn.onclick = () => linkCustomer(row.id);
    box.appendChild(btn);
  }
}

document.getElementById("customerSearch").oninput = () => {
  clearTimeout(customerSearchTimer);
  customerSearchTimer = setTimeout(() => {
    runCustomerSearch(document.getElementById("customerSearch").value).catch(() => {});
  }, 250);
};

document.getElementById("customerCreateBtn").onclick = async () => {
  const ctx = customerLinkContext();
  if (!ctx) {
    showError("ابتدا یک جلسه یا فروش بوفه را انتخاب کنید.");
    return;
  }
  const name = document.getElementById("customerName").value;
  const mobile = document.getElementById("customerMobile").value;
  const membershipCode = document.getElementById("customerCode").value || null;
  const body = {
    name,
    mobile,
    membershipCode,
    linkSessionId: ctx.sessionId || null,
    linkOpenSaleId: ctx.openSaleId || null,
  };
  const created = await api("/api/operator/customers", { method: "POST", body: JSON.stringify(body) });
  renderCustomerChip(created);
  document.getElementById("customerName").value = "";
  document.getElementById("customerMobile").value = "";
  document.getElementById("customerCode").value = "";
  await refreshGrid();
  if (ctx.sessionId && !document.getElementById("bill").classList.contains("hidden")) {
    const bill = await api(`/api/operator/sessions/${ctx.sessionId}/bill`);
    renderBill(bill);
  }
};

document.getElementById("customerClearBtn").onclick = () => clearCustomerLink().catch(() => {});

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
    await refreshLowStock();
    await refreshManualSettle();
    await refreshPendingC2c();
    setInterval(async () => {
      await refreshGrid();
      await refreshLowStock();
      await refreshPendingC2c();
    }, 10000);
  } catch (e) {
    document.getElementById("loginError").textContent = String(e.message);
    document.getElementById("loginError").classList.remove("hidden");
  }
};

document.getElementById("buffetCloseBtn").onclick = () => {
  document.getElementById("buffetPanel").classList.add("hidden");
  if (billTarget !== "buffet-only") {
    resetBuffetOnlyContext();
  }
};

document.getElementById("buffetCheckoutBtn").onclick = async () => {
  const bill = await api(`/api/operator/buffet-only/${buffetOpenSaleId}/checkout`, { method: "POST" });
  billTarget = "buffet-only";
  document.getElementById("bill").classList.remove("hidden");
  renderBill({
    collectibleToman: bill.collectibleToman,
    unroundedPayableToman: bill.unroundedPayableToman,
    roundingToman: bill.roundingToman,
    timeGrossToman: bill.timeGrossToman,
    buffetGrossToman: bill.buffetGrossToman,
    servicesGrossToman: bill.servicesGrossToman,
    saleLines: [],
    segments: [],
  });
  paymentDraft = [];
  renderPaymentLines();
};

document.getElementById("openBuffetFromBillBtn").onclick = async () => {
  if (!selectedSessionId) return;
  await openBuffetPanel({ id: selectedTile?.id || "", sessionId: selectedSessionId, openSaleId: selectedTile?.openSaleId });
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
    if (t.sessionId && (t.status === "Playing" || t.status === "Paused")) {
      const icon = document.createElement("button");
      icon.type = "button";
      icon.className = "buffet-icon";
      icon.title = "بوفه";
      icon.textContent = "🍽";
      icon.onclick = async (ev) => {
        ev.stopPropagation();
        await openBuffetPanel(t);
      };
      el.appendChild(icon);
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
    li.textContent = `#${row.invoiceNumber} — ${row.amountToman} تومان`;
    if (row.destinationCardId) li.textContent += ` → ${row.destinationCardId}`;
    const confirmBtn = document.createElement("button");
    confirmBtn.type = "button";
    confirmBtn.textContent = "تأیید";
    const rejectBtn = document.createElement("button");
    rejectBtn.type = "button";
    rejectBtn.textContent = "رد";
    confirmBtn.onclick = async () => {
      try {
        await api(`/api/operator/payments/${row.id}/confirm`, { method: "POST" });
        await refreshPendingC2c();
      } catch (_) {}
    };
    rejectBtn.onclick = async () => {
      try {
        await api(`/api/operator/payments/${row.id}/reject`, { method: "POST" });
        await refreshPendingC2c();
      } catch (_) {}
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
    btnA.textContent = "صورتحساب تا چک‌پوینت";
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

async function refreshVisibleBillAfterSaleLineChange() {
  const billEl = document.getElementById("bill");
  if (billEl.classList.contains("hidden")) {
    return;
  }
  if ((billTarget === "buffet-only" || buffetMode === "buffet-only") && buffetOpenSaleId) {
    const detail = await api(`/api/operator/buffet-only/${buffetOpenSaleId}`);
    renderBuffetOnlyCheckoutBill(detail);
    return;
  }
  if (selectedSessionId) {
    const bill = await api(`/api/operator/sessions/${selectedSessionId}/bill`);
    renderBill(bill);
  }
}

function renderBillSaleLines(lines) {
  const box = document.getElementById("billSaleLines");
  if (!box) return;
  box.replaceChildren();
  for (const line of lines || []) {
    const row = document.createElement("div");
    row.className = "sale-line";
    row.textContent = `${line.name} ×${line.quantity} = ${line.lineTotalToman} (${line.kind})`;
    box.appendChild(row);
  }
}

function renderBill(bill) {
  if (billTarget !== "buffet-only") {
    billTarget = "session";
  }
  document.getElementById("bill").classList.remove("hidden");
  const d = document.getElementById("billDetail");
  const segText = (bill.segments || [])
    .map(
      (s) =>
        `${s.segmentKey}: ${s.playerCount}p ${s.mode} @ ${s.hourlyToman ?? "?"} ` +
        `${fmtTehran(s.startUtc)}–${fmtTehran(s.endUtc)}`,
    )
    .join("\n");
  d.textContent =
    `زمان ${bill.timeGrossToman ?? 0} · بوفه ${bill.buffetGrossToman ?? 0} · خدمات ${bill.servicesGrossToman ?? 0}\n` +
    `جمع ${bill.unroundedPayableToman} + رند ${bill.roundingToman} = ${bill.collectibleToman}` +
    (segText ? `\nبخش‌ها:\n${segText}` : "");
  renderBillSaleLines(bill.saleLines);
  renderCustomerChip(bill.linkedCustomer);
  const changeEl = document.getElementById("cashChange");
  if (changeEl) {
    const cash = cashTendered();
    const collect = bill.collectibleToman ?? 0;
    changeEl.textContent =
      collect > 0 && cash > 0 ? `نقد ${cash}؛ باقی‌مانده ${Math.max(0, cash - collect)}` : "";
  }
}

function renderPaymentLines() {
  const box = document.getElementById("paymentLines");
  box.replaceChildren();
  paymentDraft.forEach((line, idx) => {
    const row = document.createElement("div");
    row.className = "pay-line";
    row.textContent = `${line.method} ${line.amountToman}`;
    const del = document.createElement("button");
    del.type = "button";
    del.textContent = "حذف";
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
  resetBuffetOnlyContext();
  billTarget = "session";
  paymentDraft = [];
  const bill = await api(`/api/operator/sessions/${sessionId}/bill`);
  renderBill(bill);
  renderPaymentLines();
}

async function onTileClick(t) {
  selectedTile = t;
  document.getElementById("sessionActions").classList.add("hidden");
  if (t.status === "Buffet only") {
    await openBuffetPanel(t);
    return;
  }
  if (t.status === "Free") {
    if (t.id === "buffet-only") {
      await openBuffetPanel(t);
      return;
    }
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
  if (!isBuffetBillVisible()) {
    document.getElementById("buffetPanel").classList.add("hidden");
    if (billTarget !== "buffet-only") {
      resetBuffetOnlyContext();
    }
  }
  document.getElementById("sessionActions").classList.remove("hidden");
  document.getElementById("sessionLabel").textContent = `${t.id} — ${t.status}`;
  if (isBuffetBillVisible()) {
    const detail = await api(`/api/operator/buffet-only/${buffetOpenSaleId}`);
    renderCustomerChip(detail.linkedCustomer);
  } else {
    renderCustomerChip(t.linkedCustomer);
  }
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
  resetBuffetOnlyContext();
  billTarget = "session";
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
  const amountToman = promptAmount("مبلغ نقد (تومان)");
  if (amountToman == null || Number.isNaN(amountToman)) return;
  paymentDraft.push({ method: "Cash", amountToman });
  renderPaymentLines();
};

document.getElementById("addCardLineBtn").onclick = () => {
  const amountToman = promptAmount("مبلغ کارتخوان");
  if (amountToman == null || Number.isNaN(amountToman)) return;
  paymentDraft.push({ method: "CardReader", amountToman });
  renderPaymentLines();
};

document.getElementById("addC2cLineBtn").onclick = () => {
  const amountToman = promptAmount("مبلغ کارت‌به‌کارت");
  if (amountToman == null || Number.isNaN(amountToman)) return;
  const destinationCardId = window.prompt("کارت مقصد", destinationCards[0]?.id || "") || undefined;
  const reference = window.prompt("ارجاع", "") || undefined;
  paymentDraft.push({ method: "CardToCard", amountToman, destinationCardId, reference });
  renderPaymentLines();
};

document.getElementById("addWalletLineBtn").onclick = () => {
  if (!displayedLinkedCustomer) {
    showError("برای پرداخت کیف پول، عضو را پیوند دهید.");
    return;
  }
  const amountToman = promptAmount("مبلغ کیف پول");
  if (amountToman == null || Number.isNaN(amountToman)) return;
  paymentDraft.push({ method: "Wallet", amountToman });
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
  let collectible;
  let payments = paymentDraft;
  if (billTarget === "buffet-only" && buffetOpenSaleId) {
    const detail = await api(`/api/operator/buffet-only/${buffetOpenSaleId}`);
    collectible = detail.bill?.collectibleToman ?? 0;
    if (collectible === 0 && !payments.length) payments = [];
    else if (payments.length === 0) payments = [{ method: "Cash", amountToman: collectible }];
    await api(`/api/operator/buffet-only/${buffetOpenSaleId}/confirm`, {
      method: "POST",
      body: JSON.stringify({ payments }),
    });
    buffetOpenSaleId = null;
    billTarget = null;
    document.getElementById("buffetPanel").classList.add("hidden");
  } else {
    const bill = await api(`/api/operator/sessions/${selectedSessionId}/bill`);
    collectible = bill.collectibleToman;
    if (collectible === 0) payments = [];
    else if (payments.length === 0) payments = [{ method: "Cash", amountToman: collectible }];
    await api(`/api/operator/sessions/${selectedSessionId}/bill/confirm`, {
      method: "POST",
      body: JSON.stringify({ payments }),
    });
  }
  selectedSessionId = null;
  billTarget = null;
  document.getElementById("bill").classList.add("hidden");
  await refreshGrid();
  await refreshLowStock();
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
  const reason = window.prompt("دلیل باطل", "") || "";
  await api(`/api/operator/invoices/${n}/void`, { method: "POST", body: JSON.stringify({ reason }) });
  document.getElementById("invoiceDetail").textContent = "باطل شد.";
};
