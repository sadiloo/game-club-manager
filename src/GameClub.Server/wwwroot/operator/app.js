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
  const balanceEl = document.getElementById("walletBalance");
  if (!linked) {
    chip.classList.add("hidden");
    chip.textContent = "";
    walletBox.classList.add("hidden");
    if (balanceEl) balanceEl.textContent = "";
    return;
  }
  const code = linked.membershipCode ? ` · کارت ${linked.membershipCode}` : "";
  chip.textContent = `پیوسته: ${linked.name} — ${linked.mobile}${code}`;
  chip.classList.remove("hidden");
  walletBox.classList.remove("hidden");
  refreshWalletBalance().catch(() => {});
}

async function refreshWalletBalance() {
  const linked = displayedLinkedCustomer;
  const el = document.getElementById("walletBalance");
  if (!linked || !el) return;
  const w = await api(`/api/operator/members/customers/${linked.id}/wallet`);
  el.textContent = `کیف پول: واقعی ${w.realToman} · هدیه ${w.giftToman} · کل ${w.spendableToman}`;
}

async function resolveLinkedCustomerForWallet() {
  if (displayedLinkedCustomer) return displayedLinkedCustomer;
  if (billTarget === "buffet-only" && buffetOpenSaleId) {
    const sale = await api(`/api/operator/buffet-only/${buffetOpenSaleId}`);
    return sale.linkedCustomer || null;
  }
  if (selectedSessionId) {
    const sale = await api(`/api/operator/sessions/${selectedSessionId}/sale`);
    return sale.linkedCustomer || null;
  }
  return null;
}

async function refreshBillPreview() {
  if (!paymentDraft.length) return;
  const payments = paymentDraft.map((line) => ({
    method: line.method,
    amountToman: line.amountToman,
    destinationCardId: line.destinationCardId,
    reference: line.reference,
  }));
  try {
    if (billTarget === "buffet-only" && buffetOpenSaleId) {
      const res = await api(`/api/operator/buffet-only/${buffetOpenSaleId}/bill/preview`, {
        method: "POST",
        body: JSON.stringify({ payments }),
      });
      renderBuffetOnlyCheckoutBill(res);
      return;
    }
    if (selectedSessionId) {
      const bill = await api(`/api/operator/sessions/${selectedSessionId}/bill/preview`, {
        method: "POST",
        body: JSON.stringify({ payments }),
      });
      renderBill(bill);
    }
  } catch (_) {}
}

async function walletTopUp(method) {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) {
    showError("عضوی پیوسته نیست.");
    return;
  }
  const raw = window.prompt("مبلغ شارژ (تومان)", "50000");
  if (!raw) return;
  const amountToman = Number(String(raw).replace(/,/g, ""));
  if (!Number.isFinite(amountToman) || amountToman <= 0) {
    showError("مبلغ نامعتبر است.");
    return;
  }
  const body = { method, amountToman };
  if (method === "CardToCard") {
    body.destinationCardId = window.prompt("کارت مقصد", destinationCards[0]?.id || "") || undefined;
    body.reference = window.prompt("ارجاع", "") || undefined;
  }
  await api(`/api/operator/members/customers/${linked.id}/wallet/top-up`, {
    method: "POST",
    body: JSON.stringify(body),
  });
  renderCustomerChip(linked);
}

const walletTopUpCashBtn = document.getElementById("walletTopUpCash");
if (walletTopUpCashBtn) walletTopUpCashBtn.onclick = () => walletTopUp("Cash").catch(() => {});
const walletTopUpCardBtn = document.getElementById("walletTopUpCard");
if (walletTopUpCardBtn) walletTopUpCardBtn.onclick = () => walletTopUp("CardReader").catch(() => {});
const walletTopUpC2cBtn = document.getElementById("walletTopUpC2c");
if (walletTopUpC2cBtn) walletTopUpC2cBtn.onclick = () => walletTopUp("CardToCard").catch(() => {});

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
    const mgr = document.getElementById("managerPanel");
    if (mgr) mgr.classList.toggle("hidden", currentRole !== "Manager" && currentRole !== "Owner");
    destinationCards = (await api("/api/operator/destination-cards")) || [];
    await refreshGrid();
    await refreshLowStock();
    await refreshManualSettle();
    await refreshPendingC2c();
    await refreshPendingWalletTopups();
    await refreshShiftState();
    await refreshCafeSettingsUi();
    setInterval(async () => {
      await refreshGrid();
      await refreshLowStock();
      await refreshPendingC2c();
      await refreshPendingWalletTopups();
    }, 10000);
    if (typeof signalR !== "undefined") {
      const hub = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/operator", { withCredentials: true })
        .withAutomaticReconnect()
        .build();
      hub.on("gridRefresh", () => refreshGrid().catch(() => {}));
      hub.start().catch(() => {});
    }
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
  selectedSessionId = null;
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
    linkedCustomer: null,
  });
  paymentDraft = [];
  renderPaymentLines();
  try {
    const sale = await api(`/api/operator/buffet-only/${buffetOpenSaleId}`);
    renderBill({
      collectibleToman: bill.collectibleToman,
      unroundedPayableToman: bill.unroundedPayableToman,
      roundingToman: bill.roundingToman,
      timeGrossToman: bill.timeGrossToman,
      buffetGrossToman: bill.buffetGrossToman,
      servicesGrossToman: bill.servicesGrossToman,
      saleLines: sale?.lines ?? [],
      segments: [],
      linkedCustomer: sale?.linkedCustomer,
    });
  } catch (_) {}
};

document.getElementById("openBuffetFromBillBtn").onclick = async () => {
  if (!selectedSessionId) return;
  await openBuffetPanel({ id: selectedTile?.id || "", sessionId: selectedSessionId, openSaleId: selectedTile?.openSaleId });
};

let lastGridTiles = [];

async function refreshPrepaidGlobalAlerts() {
  const banner = document.getElementById("prepaidGlobalAlert");
  if (!banner) return;
  const alertStations = [];
  for (const t of lastGridTiles) {
    if (!t.sessionId) continue;
    try {
      const st = await api(`/api/operator/sessions/${t.sessionId}/prepaid`);
      if (st.prepaidEndAlert) alertStations.push(t.id);
    } catch (_) {}
  }
  if (alertStations.length > 0) {
    banner.textContent = `پایان زمان: ${alertStations.join("، ")}`;
    banner.classList.remove("hidden");
    banner.classList.add("alert");
  } else {
    banner.textContent = "";
    banner.classList.add("hidden");
    banner.classList.remove("alert");
  }
}

async function refreshGrid() {
  const tiles = await api("/api/operator/grid");
  lastGridTiles = tiles;
  await refreshPrepaidGlobalAlerts();
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
  const levelDisc = bill.levelDiscountToman ?? 0;
  const bdayDisc = bill.birthdayDiscountToman ?? 0;
  const discLine =
    levelDisc || bdayDisc
      ? `\nتخفیف سطح ${levelDisc} · تولد ${bdayDisc}`
      : "";
  d.textContent =
    `زمان ${bill.timeGrossToman ?? 0} · بوفه ${bill.buffetGrossToman ?? 0} · خدمات ${bill.servicesGrossToman ?? 0}\n` +
    `جمع ${bill.unroundedPayableToman} + رند ${bill.roundingToman} = ${bill.collectibleToman}` +
    discLine +
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
      refreshBillPreview().catch(() => {});
    };
    row.appendChild(del);
    box.appendChild(row);
  });
  if (paymentDraft.length > 0) {
    refreshBillPreview().catch(() => {});
  }
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
  if (t.status === "Broken" || t.status === "Reserved") {
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
  await refreshPrepaidStatus();
  if (isBuffetBillVisible()) {
    const detail = await api(`/api/operator/buffet-only/${buffetOpenSaleId}`);
    renderCustomerChip(detail.linkedCustomer);
  } else {
    renderCustomerChip(t.linkedCustomer);
  }
  const groupKind = t.groupKind ?? "";
  const isPcStation =
    groupKind === "Pc" ||
    groupKind === "1" ||
    (t.groupId || "").startsWith("pc");
  const isPlayingPc = isPcStation && (t.status === "Playing" || t.status === "Open");
  document.getElementById("pauseBtn").classList.toggle("hidden", !(!isPcStation && t.status === "Playing"));
  document.getElementById("resumeBtn").classList.toggle("hidden", !(!isPcStation && t.status === "Paused"));
  document.getElementById("prepaidDurationBtn").classList.toggle("hidden", isPlayingPc || isPcStation);
  document.getElementById("prepaidAmountBtn").classList.toggle("hidden", isPlayingPc || isPcStation);
  document.getElementById("playerCountBtn").classList.toggle("hidden", isPcStation);
  document.getElementById("playerCountSelect").parentElement.classList.toggle("hidden", isPcStation);
  document.getElementById("forceLogoutBtn").classList.toggle("hidden", !isPlayingPc);
  document.getElementById("openBillBtn").classList.toggle("hidden", isPcStation);
  const transferVisible = t.status === "Playing" || t.status === "Paused";
  document.getElementById("transferBtn").classList.toggle("hidden", !transferVisible);
  document.getElementById("transferFaultWrap").classList.toggle("hidden", !transferVisible);
  document.getElementById("transferFaultChk").checked = false;
}

document.getElementById("transferBtn").onclick = async () => {
  const target = window.prompt("شناسه ایستگاه مقصد (مثلاً PS5-2)");
  if (target == null || !String(target).trim()) return;
  const markSourceBroken = document.getElementById("transferFaultChk").checked;
  const tile = (lastGridTiles || []).find((x) => x.sessionId === selectedSessionId);
  const commandId = (() => {
    if (typeof crypto !== "undefined" && crypto.randomUUID) return crypto.randomUUID();
    const part = () => Math.floor((1 + Math.random()) * 0x10000).toString(16).slice(1);
    return `${part()}${part()}-${part()}-4${part().slice(0, 3)}-a${part().slice(0, 3)}-${part()}${part()}${part()}`;
  })();
  try {
    await api(`/api/operator/sessions/${selectedSessionId}/transfer`, {
      method: "POST",
      body: JSON.stringify({
        targetStationId: String(target).trim(),
        markSourceBroken,
        expectedVersion: tile?.rowVersion ?? null,
        commandId,
      }),
    });
    document.getElementById("sessionActions").classList.add("hidden");
  } finally {
    await refreshGrid();
  }
};

document.getElementById("pauseBtn").onclick = async () => {
  await api(`/api/operator/sessions/${selectedSessionId}/pause`, { method: "POST" });
  await refreshGrid();
};

document.getElementById("forceLogoutBtn").onclick = async () => {
  if (!selectedSessionId) return;
  await api(`/api/operator/sessions/${selectedSessionId}/force-logout`, { method: "POST" });
  document.getElementById("sessionActions").classList.add("hidden");
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

document.getElementById("addWalletLineBtn").onclick = async () => {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) {
    showError("برای پرداخت کیف پول، عضو را پیوند دهید.");
    return;
  }
  renderCustomerChip(linked);
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

async function refreshPendingWalletTopups() {
  const ul = document.getElementById("pendingWalletList");
  if (!ul) return;
  const list = (await api("/api/operator/members/wallet/pending")) || [];
  ul.replaceChildren();
  for (const row of list) {
    const li = document.createElement("li");
    li.textContent = `#${row.id} — ${row.amountToman}`;
    const ok = document.createElement("button");
    ok.type = "button";
    ok.textContent = "تأیید";
    ok.onclick = async () => {
      await api(`/api/operator/members/wallet/pending/${row.id}/confirm`, { method: "POST" });
      await refreshPendingWalletTopups();
    };
    const no = document.createElement("button");
    no.type = "button";
    no.textContent = "رد";
    no.onclick = async () => {
      await api(`/api/operator/members/wallet/pending/${row.id}/reject`, { method: "POST" });
      await refreshPendingWalletTopups();
    };
    li.append(ok, no);
    ul.appendChild(li);
  }
}

const bindClick = (id, fn) => {
  const el = document.getElementById(id);
  if (el) el.onclick = fn;
};

async function refreshPrepaidStatus() {
  if (!selectedSessionId) return;
  const el = document.getElementById("prepaidStatus");
  if (!el) return;
  try {
    const st = await api(`/api/operator/sessions/${selectedSessionId}/prepaid`);
    const mins = Math.floor((st.remainingActiveSeconds ?? 0) / 60);
    const secs = (st.remainingActiveSeconds ?? 0) % 60;
    el.textContent =
      `رزرو: ${st.prepaidKind} · باقی ${mins}:${String(secs).padStart(2, "0")}` +
      (st.prepaidEndAlert ? " · پایان زمان" : "");
    if (st.prepaidEndAlert) {
      el.classList.add("alert");
    } else {
      el.classList.remove("alert");
    }
  } catch (_) {
    el.textContent = "";
  }
}

setInterval(() => {
  refreshPrepaidStatus().catch(() => {});
  refreshPrepaidGlobalAlerts().catch(() => {});
}, 5000);

bindClick("prepaidDurationBtn", async () => {
  if (!selectedSessionId) return;
  const minutes = Number(window.prompt("دقیقه", "60"));
  if (!Number.isFinite(minutes) || minutes <= 0) return;
  await api(`/api/operator/sessions/${selectedSessionId}/prepaid/duration`, {
    method: "POST",
    body: JSON.stringify({ minutes }),
  });
  await refreshPrepaidStatus();
});

bindClick("prepaidAmountBtn", async () => {
  if (!selectedSessionId) return;
  const amountToman = Number(String(window.prompt("مبلغ", "55000")).replace(/,/g, ""));
  if (!Number.isFinite(amountToman) || amountToman <= 0) return;
  await api(`/api/operator/sessions/${selectedSessionId}/prepaid/amount`, {
    method: "POST",
    body: JSON.stringify({ amountToman }),
  });
  await refreshPrepaidStatus();
});

bindClick("mgrGiftBtn", async () => {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) return showError("عضو پیوسته نیست.");
  await api(`/api/operator/members/customers/${linked.id}/wallet/gift`, {
    method: "POST",
    body: JSON.stringify({ giftToman: Number(window.prompt("هدیه", "10000")) }),
  });
  await refreshWalletBalance();
});

bindClick("mgrPairingCodeBtn", async () => {
  const stationId = document.getElementById("mgrPairStationId").value.trim();
  if (!stationId) return showError("شناسه ایستگاه را وارد کنید.");
  const res = await api(`/api/operator/stations/${stationId}/pairing-code`, { method: "POST" });
  showError(`کد جفت‌سازی: ${res.code}`);
});

bindClick("mgrCatalogAdminBtn", async () => {
  const games = await api("/api/operator/games");
  showError(`بازی‌های کاتالوگ: ${(games || []).length} مورد`);
});

bindClick("mgrOpeningBtn", async () => {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) return showError("عضو پیوسته نیست.");
  const realRaw = window.prompt("واقعی (خالی = بدون تغییر)", "");
  const giftRaw = window.prompt("هدیه (خالی = بدون تغییر)", "");
  const debtRaw = window.prompt("بدهی (خالی = بدون تغییر)", "");
  const hoursRaw = window.prompt("ساعت بازی (خالی = بدون تغییر)", "");
  const body = {};
  if (realRaw != null && realRaw.trim() !== "") body.realToman = Number(String(realRaw).replace(/,/g, ""));
  if (giftRaw != null && giftRaw.trim() !== "") body.giftToman = Number(String(giftRaw).replace(/,/g, ""));
  if (debtRaw != null && debtRaw.trim() !== "") body.debtToman = Number(String(debtRaw).replace(/,/g, ""));
  if (hoursRaw != null && hoursRaw.trim() !== "") body.lifetimeHours = Number(String(hoursRaw).replace(/,/g, ""));
  await api(`/api/operator/members/customers/${linked.id}/opening-balance`, {
    method: "POST",
    body: JSON.stringify(body),
  });
  await refreshWalletBalance();
});

bindClick("mgrFloorBtn", async () => {
  const raw = window.prompt("سقف نسیه (منفی)", "-50000");
  await api("/api/operator/members/settings/debt-floor", {
    method: "PUT",
    body: JSON.stringify({ floorToman: raw === "" ? null : Number(raw) }),
  });
});

bindClick("mgrBirthdayBtn", async () => {
  const percent = Number(window.prompt("درصد تولد", "10"));
  const windowKind = window.prompt("پنجره (birth_month | day_window | exact_day)", "birth_month");
  if (!windowKind) return;
  const windowDays = Number(window.prompt("روزهای پنجره", "1"));
  const anchor = window.prompt("anchor (on_and_after | around)", "on_and_after");
  await api("/api/operator/members/settings/birthday", {
    method: "PUT",
    body: JSON.stringify({ percent, windowKind, windowDays, anchor }),
  });
});

bindClick("mgrCustBirthdayBtn", async () => {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) return;
  const year = Number(window.prompt("سال شمسی", "1380"));
  const month = Number(window.prompt("ماه", "1"));
  const day = Number(window.prompt("روز", "1"));
  await api(`/api/operator/members/customers/${linked.id}/birthday`, {
    method: "PUT",
    body: JSON.stringify({ year, month, day }),
  });
});

bindClick("mgrLadderBtn", async () => {
  const cur = await api("/api/operator/members/settings/level-ladder");
  const raw = window.prompt("جدول سطح‌ها (JSON array)", JSON.stringify(cur));
  if (raw == null) return;
  const rows = JSON.parse(raw);
  await api("/api/operator/members/settings/level-ladder", { method: "PUT", body: JSON.stringify(rows) });
});

bindClick("mgrPackageBtn", async () => {
  const id = window.prompt("شناسه بسته", "pkg120");
  if (!id) return;
  const name = window.prompt("نام", "۲ ساعت");
  const priceToman = Number(String(window.prompt("قیمت", "220000")).replace(/,/g, ""));
  const minutes = Number(window.prompt("دقیقه", "120"));
  const expRaw = window.prompt("انقضا (روز، خالی = بدون انقضا)", "");
  const restrictRaw = window.prompt('محدودیت گروه (JSON آرایه، خالی = بدون محدودیت)', "");
  await api(`/api/operator/members/packages/${id}`, {
    method: "PUT",
    body: JSON.stringify({
      name,
      priceToman,
      minutes,
      expiresAfterDays: expRaw && expRaw.trim() ? Number(expRaw) : null,
      allowedGroupIdsJson: restrictRaw && restrictRaw.trim() ? restrictRaw.trim() : null,
    }),
  });
});

async function sellPackageFlow() {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) return showError("عضو پیوسته نیست.");
  const productId = window.prompt("شناسه بسته", "pkg120");
  if (!productId) return;
  const method = window.prompt("روش پرداخت (Cash | CardReader | Wallet)", "Cash");
  const amountToman = Number(String(window.prompt("مبلغ", "220000")).replace(/,/g, ""));
  await api(`/api/operator/members/customers/${linked.id}/packages/sell`, {
    method: "POST",
    body: JSON.stringify({
      productId,
      payments: [{ method, amountToman }],
    }),
  });
  await refreshWalletBalance();
}

bindClick("opSellPackageBtn", () => sellPackageFlow().catch(showError));

async function refreshShiftState() {
  const state = await api("/api/operator/shift/state");
  const el = document.getElementById("shiftState");
  if (!el) return;
  const open = state.openShift;
  el.textContent = state.shiftsEnabled
    ? open ? `شیفت باز — تنخواه ${open.openingFloatToman}` : "شیفت بسته است"
    : "حالت روزکاری (بدون شیفت)";
}

bindClick("openShiftBtn", async () => {
  const raw = window.prompt("تنخواه (تومان)", "0");
  const openingFloatToman = Number(String(raw || "0").replace(/,/g, ""));
  await api("/api/operator/shift/open", { method: "POST", body: JSON.stringify({ openingFloatToman }) });
  await refreshShiftState();
});

let closeBaseExpectedCash = 0;
let closeSettleExtraToman = 0;
let closePreviewMeta = null;

function closeDisplayExpectedCash() {
  return closeBaseExpectedCash + closeSettleExtraToman;
}

function refreshClosePreviewSummary() {
  const el = document.getElementById("closePreviewSummary");
  if (!el || !closePreviewMeta) return;
  const e = closePreviewMeta.expected || {};
  const cashExpected = closeDisplayExpectedCash();
  const dest = (closePreviewMeta.destinationCards || [])
    .map((d) => `${d.destinationCardId}: ${d.totalToman}`)
    .join("؛ ");
  const pendInv = (closePreviewMeta.pendingCardToCard || [])
    .map((p) => `فاکتور ${p.invoiceNumber ?? "—"} → ${p.destinationCardId ?? "—"} ${p.amountToman}`)
    .join("؛ ");
  const pendWal = (closePreviewMeta.pendingWalletTopUps || [])
    .map((p) => `کیف → ${p.destinationCardId ?? "—"} ${p.amountToman}`)
    .join("؛ ");
  const pendParts = [pendInv, pendWal].filter(Boolean).join(" | ");
  el.textContent =
    `مورد انتظار: نقد ${cashExpected} | کارتخوان ${e.cardReaderExpected ?? 0} | کارت‌به‌کارت تأییدشده ${e.cardToCardConfirmedExpected ?? 0} | معلق ${e.cardToCardPending ?? 0} | مقصد: ${dest || "—"} | C2C معلق: ${pendParts || "—"}`;
}

function recalcCloseExpectedCash() {
  refreshClosePreviewSummary();
}

async function jalaliToGregorian(jy, jm, jd) {
  return api(`/api/operator/shift/jalali/${jy}/${jm}/${jd}`);
}

async function gregorianToJalali(gy, gm, gd) {
  return api(`/api/operator/shift/gregorian/${gy}/${gm}/${gd}`);
}

function aggregateIncomeBreakdowns(body) {
  function collectDayRows(node) {
    if (!node) return [];
    if (Array.isArray(node.days) && node.days.length > 0) {
      return node.days.flatMap(collectDayRows);
    }
    return [node];
  }

  const dayRows = body?.days ? collectDayRows(body) : body ? [body] : [];
  const byStation = {};
  const byOperator = {};
  const destinationCards = {};

  for (const row of dayRows) {
    for (const s of row.byStation || []) {
      byStation[s.stationId] = (byStation[s.stationId] || 0) + Number(s.receivedMoneyToman || 0);
    }
    for (const o of row.byOperator || []) {
      byOperator[o.operatorId] = (byOperator[o.operatorId] || 0) + Number(o.receivedMoneyToman || 0);
    }
    for (const c of row.destinationCards || []) {
      destinationCards[c.destinationCardId] =
        (destinationCards[c.destinationCardId] || 0) + Number(c.totalToman || 0);
    }
  }

  return {
    byStation: Object.entries(byStation).map(([stationId, receivedMoneyToman]) => ({
      stationId,
      receivedMoneyToman,
    })),
    byOperator: Object.entries(byOperator).map(([operatorId, receivedMoneyToman]) => ({
      operatorId,
      receivedMoneyToman,
    })),
    destinationCards: Object.entries(destinationCards).map(([destinationCardId, totalToman]) => ({
      destinationCardId,
      totalToman,
    })),
  };
}

function gregorianDayRangeUtc(g) {
  const from = new Date(Date.UTC(g.year, g.month - 1, g.day, -3, -30, 0));
  const to = new Date(Date.UTC(g.year, g.month - 1, g.day, 20, 29, 59));
  return { fromUtc: from.toISOString(), toUtc: to.toISOString() };
}

function renderReportsTable(title, headers, rows) {
  const head = headers.map((h) => `<th>${h}</th>`).join("");
  const body = rows.map((r) => `<tr>${r.map((c) => `<td>${c}</td>`).join("")}</tr>`).join("");
  return `<h3>${title}</h3><table dir="rtl" border="1" cellpadding="4"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
}

function renderIncomeSummary(body) {
  const rollup = aggregateIncomeBreakdowns(body);
  const merged = {
    ...body,
    byStation: body.byStation?.length ? body.byStation : rollup.byStation,
    byOperator: body.byOperator?.length ? body.byOperator : rollup.byOperator,
    destinationCards: body.destinationCards?.length ? body.destinationCards : rollup.destinationCards,
  };
  const categories = renderReportsTable(
    "ستون‌های درآمد",
    ["بازی", "بوفه", "خدمات", "گرد کردن", "خدمت ویژه", "بسته", "شارژ کیف", "دریافتی", "نقد", "کارتخوان", "کارت‌به‌کارت"],
    [
      [
        merged.playToman ?? 0,
        merged.buffetToman ?? 0,
        merged.servicesToman ?? 0,
        merged.roundingToman ?? 0,
        merged.surchargeToman ?? 0,
        merged.packageSalesToman ?? 0,
        merged.walletTopUpToman ?? 0,
        merged.receivedMoneyToman ?? 0,
        merged.receivedCashToman ?? 0,
        merged.receivedCardReaderToman ?? 0,
        merged.receivedCardToCardToman ?? 0,
      ],
    ]
  );
  const byStation = (merged.byStation || []).map((s) => [s.stationId, s.receivedMoneyToman]);
  const byOperator = (merged.byOperator || []).map((o) => [o.operatorId, o.receivedMoneyToman]);
  const dest = (merged.destinationCards || []).map((d) => [d.destinationCardId, d.totalToman]);
  return (
    categories +
    renderReportsTable("بر اساس ایستگاه", ["ایستگاه", "دریافتی"], byStation.length ? byStation : [["—", 0]]) +
    renderReportsTable("بر اساس اپراتور", ["اپراتور", "دریافتی"], byOperator.length ? byOperator : [["—", 0]]) +
    renderReportsTable("کارت مقصد", ["کارت", "جمع"], dest.length ? dest : [["—", 0]])
  );
}

const historyTableHeaders = [
  "شماره",
  "وضعیت",
  "مبلغ",
  "ایستگاه",
  "تسویه‌کننده",
  "سال",
  "ماه",
  "روز",
  "پرداخت‌ها",
  "یادداشت",
  "علت باطل",
];

async function historyRowsFromApi(rows) {
  const out = [];
  for (const r of rows || []) {
    const wd = r.workingDay;
    let year = "—";
    let month = "—";
    let day = "—";
    const gy = wd?.workingDayYear ?? wd?.year;
    const gm = wd?.workingDayMonth ?? wd?.month;
    const gd = wd?.workingDayDay ?? wd?.day;
    if (gy != null && gm != null && gd != null) {
      const jalali = await gregorianToJalali(gy, gm, gd);
      year = jalali.year;
      month = jalali.month;
      day = jalali.day;
    }
    const payments = (r.payments || [])
      .map((p) => `${p.method}:${p.amountToman}:${p.status ?? ""}`)
      .join("|");
    out.push([
      r.number,
      r.status,
      r.collectibleToman,
      r.stationId ?? "—",
      r.settledByUserId ?? "—",
      year,
      month,
      day,
      payments,
      r.operatorNote ?? "",
      r.voidReason ?? "",
    ]);
  }
  return out;
}

async function historyQuery() {
  const q = new URLSearchParams();
  const fromJy = Number(document.getElementById("histFromJy")?.value);
  const fromJm = Number(document.getElementById("histFromJm")?.value);
  const fromJd = Number(document.getElementById("histFromJd")?.value);
  const toJy = Number(document.getElementById("histToJy")?.value);
  const toJm = Number(document.getElementById("histToJm")?.value);
  const toJd = Number(document.getElementById("histToJd")?.value);
  if (fromJy && fromJm && fromJd) {
    const g = await jalaliToGregorian(fromJy, fromJm, fromJd);
    q.set("fromUtc", gregorianDayRangeUtc(g).fromUtc);
  }
  if (toJy && toJm && toJd) {
    const g = await jalaliToGregorian(toJy, toJm, toJd);
    q.set("toUtc", gregorianDayRangeUtc(g).toUtc);
  }
  const st = document.getElementById("histStation")?.value?.trim();
  const cust = document.getElementById("histCustomer")?.value?.trim();
  const oper = document.getElementById("histOperator")?.value?.trim();
  const meth = document.getElementById("histMethod")?.value?.trim();
  if (st) q.set("stationId", st);
  if (cust) q.set("customerId", cust);
  if (oper) q.set("operatorId", oper);
  if (meth) q.set("method", meth);
  const s = q.toString();
  return s ? `?${s}` : "";
}

function renderCloseSessions(openSessions) {
  const host = document.getElementById("closeSessionList");
  if (!host) return;
  closeSettleExtraToman = 0;
  if (!openSessions?.length) {
    host.innerHTML = "<p>جلسهٔ بازی باز نیست.</p>";
    return;
  }
  host.innerHTML = openSessions
    .map(
      (s) => `<div data-session="${s.sessionId}">
        <strong>${s.stationId}</strong> — ${s.status}
        <button type="button" data-action="settle" data-session="${s.sessionId}">تسویه در همین بستن</button>
        <button type="button" data-action="carry" data-session="${s.sessionId}">انتقال به دوره بعد</button>
      </div>`
    )
    .join("");
  host.querySelectorAll("button[data-action]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const sid = btn.getAttribute("data-session");
      const row = host.querySelector(`div[data-session="${sid}"]`);
      if (!row) return;
      const action = btn.getAttribute("data-action");
      if (action === "settle") {
        try {
          const bill = await api(`/api/operator/sessions/${sid}/close-settle-preview`);
          const prev = Number(row.dataset.settleAmount || 0);
          const next = Number(bill.collectibleToman || 0);
          closeSettleExtraToman += next - prev;
          row.dataset.settleAmount = String(next);
          row.dataset.choice = "settle";
          row.style.outline = "2px solid #1565c0";
          recalcCloseExpectedCash();
        } catch (e) {
          showError(String(e.message || e));
        }
      } else {
        const prev = Number(row.dataset.settleAmount || 0);
        if (row.dataset.choice === "settle") {
          closeSettleExtraToman -= prev;
          delete row.dataset.settleAmount;
          recalcCloseExpectedCash();
        }
        row.dataset.choice = "carry";
        row.style.outline = "2px solid #1565c0";
      }
    });
  });
}

function collectSessionActions() {
  const host = document.getElementById("closeSessionList");
  if (!host) return [];
  const rows = [...host.querySelectorAll("div[data-session]")];
  if (rows.length === 0) return [];
  if (rows.some((row) => !row.dataset.choice)) {
    return null;
  }
  return rows.map((row) => ({
    sessionId: row.getAttribute("data-session"),
    action: row.dataset.choice,
  }));
}

bindClick("closePreviewBtn", async () => {
  const preview = await api("/api/operator/shift/close/preview");
  renderCloseSessions(preview.openSessions);
  const e = preview.expected || {};
  closeBaseExpectedCash = Number(e.cashExpected ?? 0);
  closeSettleExtraToman = 0;
  closePreviewMeta = preview;
  refreshClosePreviewSummary();
  document.getElementById("closeReader").value = e.cardReaderExpected ?? 0;
});

bindClick("closeShiftBtn", async () => {
  const sessionActions = collectSessionActions();
  if (sessionActions === null) {
    showError("برای هر جلسهٔ باز باید «تسویه» یا «انتقال» را انتخاب کنید.");
    return;
  }
  const jy = Number(document.getElementById("closeJy").value);
  const jm = Number(document.getElementById("closeJm").value);
  const jd = Number(document.getElementById("closeJd").value);
  const g = await jalaliToGregorian(jy, jm, jd);
  const actualCashToman = Number(String(document.getElementById("closeCash").value || "0").replace(/,/g, ""));
  const actualCardReaderToman = Number(String(document.getElementById("closeReader").value || "0").replace(/,/g, ""));
  const discrepancyExplanation = document.getElementById("closeExpl").value.trim() || null;
  await api("/api/operator/shift/close", {
    method: "POST",
    body: JSON.stringify({
      workingDay: g,
      actualCashToman,
      actualCardReaderToman,
      discrepancyExplanation,
      sessionActions,
    }),
  });
  await refreshShiftState();
  document.getElementById("closePreviewSummary").textContent = "";
  renderCloseSessions([]);
});

bindClick("saveCafeSettingsBtn", async () => {
  await api("/api/operator/shift/settings", {
    method: "PUT",
    body: JSON.stringify({
      shiftsEnabled: document.getElementById("shiftsEnabledToggle").checked,
      multiOperatorEnabled: document.getElementById("multiOperatorToggle").checked,
    }),
  });
});

bindClick("dailyIncomeBtn", async () => {
  const jy = Number(document.getElementById("repJy").value);
  const jm = Number(document.getElementById("repJm").value);
  const jd = Number(document.getElementById("repJd").value);
  const g = await jalaliToGregorian(jy, jm, jd);
  const body = await api(`/api/operator/reports/income/daily?year=${g.year}&month=${g.month}&day=${g.day}`);
  document.getElementById("reportsOut").innerHTML = renderIncomeSummary(body);
});
bindClick("monthlyIncomeBtn", async () => {
  const jy = Number(document.getElementById("repJy").value);
  const jm = Number(document.getElementById("repJm").value);
  const body = await api(`/api/operator/reports/income/monthly?year=${jy}&month=${jm}`);
  document.getElementById("reportsOut").innerHTML = renderIncomeSummary(body);
});
bindClick("yearlyIncomeBtn", async () => {
  const jy = Number(document.getElementById("repJy").value);
  const body = await api(`/api/operator/reports/income/yearly?year=${jy}`);
  document.getElementById("reportsOut").innerHTML = renderIncomeSummary(body);
});
bindClick("usageBtn", async () => {
  const from = await jalaliToGregorian(
    Number(document.getElementById("usageFromJy").value),
    Number(document.getElementById("usageFromJm").value),
    Number(document.getElementById("usageFromJd").value)
  );
  const to = await jalaliToGregorian(
    Number(document.getElementById("usageToJy").value),
    Number(document.getElementById("usageToJm").value),
    Number(document.getElementById("usageToJd").value)
  );
  const range = gregorianDayRangeUtc(from);
  const rangeTo = gregorianDayRangeUtc(to);
  const body = await api(
    `/api/operator/reports/usage?fromUtc=${encodeURIComponent(range.fromUtc)}&toUtc=${encodeURIComponent(rangeTo.toUtc)}`
  );
  const stations = (body.byStation || []).map((s) => [s.stationId, s.durationSeconds]);
  const peaks = (body.peakHours || []).map((h) => [h.hour, h.durationSeconds]);
  document.getElementById("reportsOut").innerHTML =
    renderReportsTable("مصرف ایستگاه (ثانیه)", ["ایستگاه", "مدت"], stations.length ? stations : [["—", 0]]) +
    renderReportsTable(
      "ساعات اوج تهران",
      ["ساعت", "مدت (ثانیه)"],
      peaks.length ? peaks : [["—", 0]]
    );
});
bindClick("cu8Btn", async () => {
  const body = await api("/api/operator/reports/wallet/cu8");
  document.getElementById("reportsOut").innerHTML = renderReportsTable("CU-8", ["نقد", "هدیه", "هدایای صادرشده"], [
    [body.realMoneyLiabilityToman, body.giftCreditLiabilityToman, body.giftsIssuedToman],
  ]);
});
bindClick("historyBtn", async () => {
  const rows = await api(`/api/operator/reports/history${await historyQuery()}`);
  const table = await historyRowsFromApi(rows);
  document.getElementById("reportsOut").innerHTML = renderReportsTable(
    "تاریخچه",
    historyTableHeaders,
    table.length ? table : [historyTableHeaders.map(() => "—")]
  );
});
bindClick("exportHistoryBtn", async () => {
  const res = await fetch(`/api/operator/reports/history/export${await historyQuery()}`, { credentials: "include" });
  const text = await res.text();
  document.getElementById("reportsOut").innerHTML = `<h3>خروجی CSV</h3><pre>${text.replace(/</g, "&lt;")}</pre>`;
});

async function refreshCafeSettingsUi() {
  const state = await api("/api/operator/shift/state");
  const isMgr = currentRole === "Manager" || currentRole === "Owner";
  document.getElementById("cafeSettings").classList.toggle("hidden", !isMgr);
  document.getElementById("reportsPanel").classList.toggle("hidden", !isMgr);
  if (isMgr) {
    document.getElementById("shiftsEnabledToggle").checked = !!state.shiftsEnabled;
    document.getElementById("multiOperatorToggle").checked = !!state.multiOperatorEnabled;
  }
}
