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
let walletTopUpCommandId = null;
let walletC2cCommandId = null;
let walletCorrectCommandId = null;
let walletTopUpInFlight = false;
let walletTopUpDraftKey = null;
let walletCorrectDraftKey = null;

const PENDING_ACTION_TIMEOUT_MS = 15000;
const pendingRowInFlight = new Set();

function pendingRowKey(kind, id) {
  return `${kind}:${id}`;
}

function isPendingRowInFlight(kind, id) {
  return pendingRowInFlight.has(pendingRowKey(kind, id));
}

function newCommandId() {
  if (typeof crypto !== "undefined" && crypto.randomUUID) return crypto.randomUUID();
  const part = () => Math.floor((1 + Math.random()) * 0x10000).toString(16).slice(1);
  return `${part()}${part()}-${part()}-4${part().slice(0, 3)}-a${part().slice(0, 3)}-${part()}${part()}${part()}`;
}

function buildWalletTopUpDraftKey(linkedId, method, amountToman, giftToman, destinationCardId) {
  return JSON.stringify({
    customerId: linkedId,
    method,
    amountToman,
    giftToman,
    destinationCardId: destinationCardId ?? null,
  });
}

function resetWalletTopUpForm() {
  walletTopUpCommandId = null;
  walletC2cCommandId = null;
  walletTopUpDraftKey = null;
}

function resetWalletCorrectForm() {
  walletCorrectCommandId = null;
  walletCorrectDraftKey = null;
}

function ensureWalletTopUpCommandIdForDraft(draftKey) {
  if (!walletTopUpCommandId || walletTopUpDraftKey !== draftKey) {
    walletTopUpCommandId = newCommandId();
    walletC2cCommandId = null;
  }
  walletTopUpDraftKey = draftKey;
}

function ensureWalletCorrectCommandIdForDraft(draftKey) {
  if (!walletCorrectCommandId || walletCorrectDraftKey !== draftKey) {
    walletCorrectCommandId = newCommandId();
  }
  walletCorrectDraftKey = draftKey;
}

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
  const { background, ...fetchOptions } = options;
  const isBackground = background === true;
  if (!isBackground) {
    showError("");
  }
  const res = await fetch(path, {
    credentials: "include",
    headers: { "Content-Type": "application/json", ...(fetchOptions.headers || {}) },
    ...fetchOptions,
  });
  const text = await res.text();
  if (!res.ok) {
    let msg = text;
    let code;
    try {
      const j = JSON.parse(text);
      code = j.code;
      msg = j.message || j.code || text;
    } catch (_) {}
    if (res.status === 409 && code === "not_pending") {
      msg = "این مورد قبلاً انجام شده است.";
    }
    showError(msg);
    const err = new Error(msg);
    err.status = res.status;
    err.code = code;
    throw err;
  }
  if (res.status === 204 || !text) return null;
  return JSON.parse(text);
}

async function loadCatalog() {
  catalog = (await api("/api/operator/catalog")) || [];
}

async function refreshLowStock() {
  const rows = (await api("/api/operator/catalog/low-stock", { background: true })) || [];
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
  if (!raw) {
    resetWalletTopUpForm();
    return;
  }
  const amountToman = Number(String(raw).replace(/,/g, ""));
  if (!Number.isFinite(amountToman) || amountToman <= 0) {
    showError("مبلغ نامعتبر است.");
    resetWalletTopUpForm();
    return;
  }
  const giftRaw = window.prompt("اعتبار هدیه (تومان، خالی = ۰)", "");
  const giftToman = giftRaw && giftRaw.trim() ? Number(String(giftRaw).replace(/,/g, "")) : 0;
  if (!Number.isFinite(giftToman) || giftToman < 0) {
    showError("هدیه نامعتبر است.");
    resetWalletTopUpForm();
    return;
  }
  let destinationCardId;
  if (method === "CardToCard") {
    destinationCardId = window.prompt("کارت مقصد", destinationCards[0]?.id || "") || undefined;
    if (!destinationCardId) {
      resetWalletTopUpForm();
      return;
    }
  }
  const draftKey = buildWalletTopUpDraftKey(linked.id, method, amountToman, giftToman, destinationCardId);
  ensureWalletTopUpCommandIdForDraft(draftKey);
  const commandId = method === "CardToCard"
    ? (walletC2cCommandId ||= walletTopUpCommandId)
    : walletTopUpCommandId;
  const body = {
    method,
    amountToman,
    giftToman,
    commandId,
  };
  if (method === "CardToCard") {
    body.destinationCardId = destinationCardId;
    body.reference = window.prompt("ارجاع", "") || undefined;
  }
  if (walletTopUpInFlight) {
    return;
  }
  walletTopUpInFlight = true;
  setWalletTopUpButtonsDisabled(true);
  try {
    await api(`/api/operator/members/customers/${linked.id}/wallet/top-up`, {
      method: "POST",
      body: JSON.stringify(body),
    });
    renderCustomerChip(linked);
    resetWalletTopUpForm();
  } finally {
    walletTopUpInFlight = false;
    setWalletTopUpButtonsDisabled(false);
  }
}

function setWalletTopUpButtonsDisabled(disabled) {
  for (const id of ["walletTopUpCash", "walletTopUpCard", "walletTopUpC2c"]) {
    const el = document.getElementById(id);
    if (el) {
      el.disabled = disabled;
    }
  }
}

const walletTopUpCashBtn = document.getElementById("walletTopUpCash");
if (walletTopUpCashBtn) {
  walletTopUpCashBtn.onclick = () => {
    walletTopUp("Cash").catch(() => {});
  };
}
const walletTopUpCardBtn = document.getElementById("walletTopUpCard");
if (walletTopUpCardBtn) {
  walletTopUpCardBtn.onclick = () => {
    walletTopUp("CardReader").catch(() => {});
  };
}
const walletTopUpC2cBtn = document.getElementById("walletTopUpC2c");
if (walletTopUpC2cBtn) {
  walletTopUpC2cBtn.onclick = () => {
    walletTopUp("CardToCard").catch(() => {});
  };
}

async function walletCorrectTopUp() {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) {
    showError("عضوی پیوسته نیست.");
    return;
  }
  const list = await api(`/api/operator/members/customers/${linked.id}/wallet/top-ups`);
  if (!list?.length) {
    window.alert("شارژ قابل اصلاحی نیست (موقت)");
    return;
  }
  const lines = list.map(
    (t, i) =>
      `${i + 1}: ${t.method} ${t.currentAmountToman} + هدیه ${t.currentGiftToman ?? 0} (${t.topUpGroupId})`,
  );
  const pick = window.prompt(`شماره شارژ برای اصلاح (موقت):\n${lines.join("\n")}`, "1");
  if (!pick) {
    resetWalletCorrectForm();
    return;
  }
  const idx = Number(pick) - 1;
  if (!Number.isFinite(idx) || idx < 0 || idx >= list.length) {
    showError("انتخاب نامعتبر");
    resetWalletCorrectForm();
    return;
  }
  const row = list[idx];
  const amountRaw = window.prompt("مبلغ پرداختی درست (تومان)", String(row.currentAmountToman));
  if (amountRaw == null) {
    resetWalletCorrectForm();
    return;
  }
  const giftRaw = window.prompt("هدیه درست (تومان)", String(row.currentGiftToman ?? 0));
  if (giftRaw == null) {
    resetWalletCorrectForm();
    return;
  }
  const description = window.prompt("توضیح (اجباری)", "");
  if (!description?.trim()) {
    showError("توضیح الزامی است.");
    resetWalletCorrectForm();
    return;
  }
  const newAmountToman = Number(String(amountRaw).replace(/,/g, ""));
  const newGiftToman = Number(String(giftRaw).replace(/,/g, ""));
  const draftKey = JSON.stringify({
    topUpGroupId: row.topUpGroupId,
    newAmountToman,
    newGiftToman,
    description: description.trim(),
  });
  ensureWalletCorrectCommandIdForDraft(draftKey);
  const correctBtn = document.getElementById("walletCorrectBtn");
  if (correctBtn) {
    correctBtn.disabled = true;
  }
  try {
    await api(`/api/operator/members/wallet/top-ups/${row.topUpGroupId}/correct`, {
      method: "POST",
      body: JSON.stringify({
        newAmountToman,
        newGiftToman,
        description: description.trim(),
        commandId: walletCorrectCommandId,
      }),
    });
    renderCustomerChip(linked);
    window.alert("اصلاح ثبت شد (موقت)");
    resetWalletCorrectForm();
  } finally {
    if (correctBtn) {
      correctBtn.disabled = false;
    }
  }
}

const walletCorrectBtn = document.getElementById("walletCorrectBtn");
if (walletCorrectBtn) {
  walletCorrectBtn.onclick = () => {
    walletCorrectTopUp().catch(() => {});
  };
}

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
    const pollMs =
      typeof window !== "undefined" && window.__operatorPollIntervalMs != null
        ? window.__operatorPollIntervalMs
        : 10000;
    setInterval(async () => {
      await refreshGrid();
      await refreshLowStock();
      await refreshPendingC2c();
      await refreshPendingWalletTopups();
    }, pollMs);
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
      const st = await api(`/api/operator/sessions/${t.sessionId}/prepaid`, { background: true });
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
  const tiles = await api("/api/operator/grid", { background: true });
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
    if (t.status === "FreeUse" && t.freeUse) {
      const fu = t.freeUse;
      if (fu.noContact) {
        el.appendChild(document.createTextNode("بی‌ارتباط"));
        el.title =
          "سرور بیش از ۳ دقیقه است از این سیستم خبر ندارد. زمان تا آخرین ارتباط حساب شده است.";
        if (fu.startedAtUtc && fu.lastContactUtc) {
          el.appendChild(document.createElement("br"));
          el.appendChild(
            document.createTextNode(kioskOpFormatElapsedBetween(fu.startedAtUtc, fu.lastContactUtc))
          );
        }
      } else {
        el.appendChild(document.createTextNode("رایگان"));
        if (fu.startedAtUtc) {
          el.appendChild(document.createElement("br"));
          el.appendChild(document.createTextNode(kioskOpFormatElapsed(fu.startedAtUtc)));
        }
      }
    } else if (t.status === "FreeUse") {
      el.appendChild(document.createTextNode("رایگان"));
    } else {
      el.appendChild(document.createTextNode(t.status));
    }
    if (t.amountToman != null) {
      el.appendChild(document.createElement("br"));
      el.appendChild(document.createTextNode(String(t.amountToman)));
    }
    if (typeof ss14RenderTileGameBadge === "function") {
      ss14RenderTileGameBadge(el, t);
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

function bindPendingListAction(button, kind, rowId, run, refreshList) {
  button.onclick = async () => {
    if (button.disabled) return;
    const key = pendingRowKey(kind, rowId);
    if (pendingRowInFlight.has(key)) return;
    pendingRowInFlight.add(key);
    const li = button.closest("li");
    if (li) {
      for (const b of li.querySelectorAll("button")) {
        b.disabled = true;
      }
    } else {
      button.disabled = true;
    }
    const timeoutMs =
      typeof window !== "undefined" && window.__pendingActionTimeoutMs != null
        ? window.__pendingActionTimeoutMs
        : PENDING_ACTION_TIMEOUT_MS;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      await run(controller.signal);
    } catch (e) {
      if (e && e.status === 409 && e.code === "not_pending") {
        await refreshList();
      } else {
        if (e.name === "AbortError") {
          showError("زمان درخواست تمام شد؛ دوباره تلاش کنید.");
        } else if (e instanceof TypeError) {
          showError("خطای شبکه؛ دوباره تلاش کنید.");
        } else {
          showError(String(e.message || e) || "خطا در انجام عملیات.");
        }
        if (li && li.isConnected) {
          for (const b of li.querySelectorAll("button")) {
            b.disabled = false;
          }
        }
      }
    } finally {
      clearTimeout(timer);
      pendingRowInFlight.delete(key);
    }
  };
}

async function refreshPendingC2c() {
  const list = await api("/api/operator/payments/pending-card-to-card", { background: true });
  const ul = document.getElementById("pendingC2cList");
  ul.replaceChildren();
  for (const row of list || []) {
    const li = document.createElement("li");
    li.textContent = `#${row.invoiceNumber} — ${row.amountToman} تومان`;
    if (row.destinationCardId) li.textContent += ` → ${row.destinationCardId}`;
    const inFlight = isPendingRowInFlight("c2c", row.id);
    const confirmBtn = document.createElement("button");
    confirmBtn.type = "button";
    confirmBtn.textContent = "تأیید";
    confirmBtn.disabled = inFlight;
    const rejectBtn = document.createElement("button");
    rejectBtn.type = "button";
    rejectBtn.textContent = "رد";
    rejectBtn.disabled = inFlight;
    bindPendingListAction(confirmBtn, "c2c", row.id, async (signal) => {
      await api(`/api/operator/payments/${row.id}/confirm`, { method: "POST", signal });
      await refreshPendingC2c();
    }, refreshPendingC2c);
    bindPendingListAction(rejectBtn, "c2c", row.id, async (signal) => {
      await api(`/api/operator/payments/${row.id}/reject`, { method: "POST", signal });
      await refreshPendingC2c();
    }, refreshPendingC2c);
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
    .map((s) => {
      if (s.displayLine) {
        return `${s.segmentKey}: ${s.displayLine}`;
      }
      const rate = typeof ss14FmtToman === "function" ? ss14FmtToman(s.hourlyToman) : s.hourlyToman ?? "?";
      return `${s.segmentKey}: ${s.playerCount}p ${s.mode} @ ${rate} ` + `${fmtTehran(s.startUtc)}–${fmtTehran(s.endUtc)}`;
    })
    .join("\n");
  const levelDisc = bill.levelDiscountToman ?? 0;
  const bdayDisc = bill.birthdayDiscountToman ?? 0;
  const discLine =
    levelDisc || bdayDisc
      ? `\nتخفیف سطح ${levelDisc} · تولد ${bdayDisc}`
      : "";
  const writeOff = bill.writeOffToman ?? 0;
  const burnLine = writeOff > 0 ? `\nسوخت‌شده ${writeOff}` : "";
  d.textContent =
    `زمان ${bill.timeGrossToman ?? 0} · بوفه ${bill.buffetGrossToman ?? 0} · خدمات ${bill.servicesGrossToman ?? 0}\n` +
    `جمع ${bill.unroundedPayableToman} + رند ${bill.roundingToman} = ${bill.collectibleToman}` +
    discLine +
    burnLine +
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
    if (typeof ss14StartConsoleStation === "function") {
      await ss14StartConsoleStation(t);
    } else {
      await api(`/api/operator/stations/${encodeURIComponent(t.id)}/start`, { method: "POST" });
    }
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
  if (typeof ss14RefreshSessionGameUi === "function") {
    ss14RefreshSessionGameUi(t, isPcStation);
  }
  if (typeof ss14FilterPlayerCountSelect === "function") {
    ss14FilterPlayerCountSelect(t);
  }
}

document.getElementById("transferBtn").onclick = async () => {
  const tile = (lastGridTiles || []).find((x) => x.sessionId === selectedSessionId);
  if (typeof ss14ConfirmTransferIfGame === "function") {
    const ok = await ss14ConfirmTransferIfGame(tile);
    if (!ok) return;
  }
  const target = window.prompt("شناسه ایستگاه مقصد (مثلاً PS5-2)");
  if (target == null || !String(target).trim()) return;
  const markSourceBroken = document.getElementById("transferFaultChk").checked;
  const commandId = newCommandId();
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
  const writeOff = inv.writeOffToman ?? 0;
  const burnLine = writeOff > 0 ? `\nسوخت‌شده ${writeOff}` : "";
  document.getElementById("invoiceDetail").textContent =
    `زمان ${inv.timeGrossToman ?? 0} · بوفه ${inv.buffetGrossToman ?? 0} · خدمات ${inv.servicesGrossToman ?? 0}\n` +
    `جمع ${inv.unroundedPayableToman ?? 0} + رند ${inv.roundingToman ?? 0} = ${inv.collectibleToman ?? 0}` +
    burnLine;
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
  const list = (await api("/api/operator/members/wallet/pending", { background: true })) || [];
  ul.replaceChildren();
  for (const row of list) {
    const li = document.createElement("li");
    li.textContent = `#${row.id} — ${row.amountToman}`;
    const inFlight = isPendingRowInFlight("topup", row.id);
    const ok = document.createElement("button");
    ok.type = "button";
    ok.textContent = "تأیید";
    ok.disabled = inFlight;
    bindPendingListAction(ok, "topup", row.id, async (signal) => {
      await api(`/api/operator/members/wallet/pending/${row.id}/confirm`, { method: "POST", signal });
      await refreshPendingWalletTopups();
    }, refreshPendingWalletTopups);
    const no = document.createElement("button");
    no.type = "button";
    no.textContent = "رد";
    no.disabled = inFlight;
    bindPendingListAction(no, "topup", row.id, async (signal) => {
      await api(`/api/operator/members/wallet/pending/${row.id}/reject`, { method: "POST", signal });
      await refreshPendingWalletTopups();
    }, refreshPendingWalletTopups);
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
    const st = await api(`/api/operator/sessions/${selectedSessionId}/prepaid`, { background: true });
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

const prepaidRefreshMs =
  typeof window !== "undefined" && window.__prepaidRefreshIntervalMs != null
    ? window.__prepaidRefreshIntervalMs
    : 5000;
setInterval(() => {
  refreshPrepaidStatus().catch(() => {});
  refreshPrepaidGlobalAlerts().catch(() => {});
}, prepaidRefreshMs);

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

function populateCatalogEditor(entry) {
  document.getElementById("catalogEditId").value = entry ? String(entry.id) : "";
  document.getElementById("catalogName").value = entry?.name ?? "";
  document.getElementById("catalogCategoryId").value = String(entry?.categoryId ?? 1);
  document.getElementById("catalogExecutable").value = entry?.executableOrUri ?? "";
  document.getElementById("catalogArgs").value = entry?.arguments ?? "";
  document.getElementById("catalogIcon").value = entry?.iconUrl ?? "";
  document.getElementById("catalogOrder").value = String(entry?.sortOrder ?? 0);
  document.getElementById("catalogEnabled").checked = entry?.enabled !== false;
  document.getElementById("catalogGroups").value = (entry?.groupRestrictions ?? []).join(",");
  document.getElementById("catalogAllowlist").value = (entry?.allowedProcessNames ?? []).join(",");
}

async function refreshCatalogAdminPanel() {
  const box = document.getElementById("catalogAdminPanel");
  if (!box) return;
  const summary = document.getElementById("catalogAdminSummary");
  const list = document.getElementById("catalogAdminEntries");
  const catalog = await api("/api/operator/games");
  const entries = catalog.entries || [];
  box.dataset.catalogVersion = String(catalog.catalogVersion ?? 0);
  if (summary) {
    summary.textContent = `نسخه ${catalog.catalogVersion ?? 0} · ${entries.length} مورد`;
  }
  if (!list) return;
  list.innerHTML = "";
  entries.forEach((entry) => {
    const row = document.createElement("div");
    row.className = "sale-line";
    row.style.cursor = "pointer";
    row.textContent = `${entry.id}: ${entry.name} · ${entry.enabled ? "فعال" : "غیرفعال"} · order ${entry.sortOrder ?? 0}`;
    row.addEventListener("click", () => populateCatalogEditor(entry));
    list.appendChild(row);
  });
}

bindClick("mgrCatalogAdminBtn", async () => {
  await refreshCatalogAdminPanel();
  const panel = document.getElementById("catalogAdminPanel");
  if (panel) panel.classList.remove("hidden");
});

bindClick("catalogSaveBtn", async () => {
  const name = document.getElementById("catalogName").value.trim();
  const categoryId = Number(document.getElementById("catalogCategoryId").value || "1");
  const executable = document.getElementById("catalogExecutable").value.trim();
  const args = document.getElementById("catalogArgs").value.trim();
  const icon = document.getElementById("catalogIcon").value.trim();
  const order = Number(document.getElementById("catalogOrder").value || "0");
  const enabled = document.getElementById("catalogEnabled").checked;
  const allowlist = document
    .getElementById("catalogAllowlist")
    .value.split(",")
    .map((v) => v.trim())
    .filter(Boolean);
  const groups = document
    .getElementById("catalogGroups")
    .value.split(",")
    .map((v) => v.trim())
    .filter(Boolean);
  const editId = document.getElementById("catalogEditId").value.trim();
  if (editId) {
    await api(`/api/operator/games/${editId}`, {
      method: "PUT",
      body: JSON.stringify({
        name,
        categoryId,
        enabled,
        executableOrUri: executable,
        arguments: args || null,
        iconUrl: icon || null,
        sortOrder: order,
        groupRestrictions: groups,
        allowedProcessNames: allowlist,
      }),
    });
  } else {
    await api("/api/operator/games", {
      method: "POST",
      body: JSON.stringify({
        name,
        categoryId,
        executableOrUri: executable,
        arguments: args || null,
        iconUrl: icon || null,
        sortOrder: order,
        enabled,
        groupRestrictions: groups,
        allowedProcessNames: allowlist,
      }),
    });
  }
  await refreshCatalogAdminPanel();
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

bindClick("mgrGiftCapBtn", async () => {
  const cur = await api("/api/operator/members/settings/operator-gift-cap");
  const maxToman = Number(String(window.prompt("سقف مبلغ هدیه (تومان)", String(cur.maxToman ?? 0))).replace(/,/g, ""));
  const maxPercent = Number(window.prompt("سقف درصد هدیه", String(cur.maxPercent ?? 0)));
  await api("/api/operator/members/settings/operator-gift-cap", {
    method: "PUT",
    body: JSON.stringify({ maxToman, maxPercent }),
  });
});

bindClick("mgrCorrectionsBtn", async () => {
  const rows = await api("/api/operator/members/wallet/corrections");
  const text = (rows || [])
    .map((r) => `${r.atUtc}: ${r.beforeAmountToman}→${r.afterAmountToman} هدیه ${r.beforeGiftToman}→${r.afterGiftToman}`)
    .join("\n");
  window.alert(text || "اصلاحی ثبت نشده");
});

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
  const recv = closePreviewMeta.received || {};
  const post = closePreviewMeta.postCloseCorrections || {};
  const giftIssued = closePreviewMeta.giftIssuedToman ?? 0;
  el.textContent =
    `مورد انتظار: نقد ${cashExpected} | کارتخوان ${e.cardReaderExpected ?? 0} | کارت‌به‌کارت تأییدشده ${e.cardToCardConfirmedExpected ?? 0} | معلق ${e.cardToCardPending ?? 0} | دریافتی: نقد ${recv.cashReceived ?? "—"} | کارتخوان ${recv.cardReaderReceived ?? "—"} | هدیه داده‌شده ${giftIssued} | تعدیل اصلاح شارژ (موقت): نقد ${post.cash ?? 0} کارت ${post.card ?? 0} C2C ${post.c2c ?? 0} هدیه ${post.gift ?? 0} | مقصد: ${dest || "—"} | C2C معلق: ${pendParts || "—"}`;
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
    ["بازی", "بوفه", "خدمات", "گرد کردن", "خدمت ویژه", "هدیه داده‌شده", "شارژ کیف", "دریافتی", "نقد", "کارتخوان", "کارت‌به‌کارت"],
    [
      [
        merged.playToman ?? 0,
        merged.buffetToman ?? 0,
        merged.servicesToman ?? 0,
        merged.roundingToman ?? 0,
        merged.surchargeToman ?? 0,
        merged.giftIssuedToman ?? 0,
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
    const idle = document.getElementById("mgrFreeIdleMinutes");
    if (idle && state.kioskFreeUseIdleMinutes != null) {
      idle.value = String(state.kioskFreeUseIdleMinutes).replace(/\d/g, (d) => "۰۱۲۳۴۵۶۷۸۹"[d]);
    }
  }
  if (typeof ss14RefreshManagerGameUi === "function") {
    ss14RefreshManagerGameUi(isMgr);
  }
}

// --- SS-14 game tariff ---
function ss14IsConsoleTile(t) {
  const gk = t?.groupKind ?? "";
  return !(gk === "Pc" || gk === "1" || String(t?.groupId || "").startsWith("pc"));
}

let ss14GamePickerEl = null;

function ss14EnsureGamePicker() {
  if (ss14GamePickerEl) return ss14GamePickerEl;
  const el = document.createElement("div");
  el.id = "ss14GamePicker";
  el.className = "panel hidden";
  // temp text
  el.innerHTML =
    '<h3>انتخاب بازی</h3><p id="ss14GamePickerHint"></p><select id="ss14GameSelect" size="8"></select><div><button type="button" id="ss14GameOkBtn">تأیید</button><button type="button" id="ss14GameCancelBtn">انصراف</button></div>';
  document.body.appendChild(el);
  ss14GamePickerEl = el;
  return el;
}

async function ss14FetchGamesForOption(mode, playerCount) {
  const qs = new URLSearchParams({ mode: String(mode), playerCount: String(playerCount) });
  return api(`/api/operator/game-titles/selectable?${qs}`, { background: true });
}

async function ss14ShowGamePicker({ hint, mode, playerCount, onConfirm }) {
  const panel = ss14EnsureGamePicker();
  panel.classList.remove("hidden");
  document.getElementById("ss14GamePickerHint").textContent = hint || "";
  const select = document.getElementById("ss14GameSelect");
  select.replaceChildren();
  const none = document.createElement("option");
  none.value = "";
  none.textContent = "بدون بازی خاص";
  select.appendChild(none);
  const games = await ss14FetchGamesForOption(mode, playerCount);
  for (const g of games) {
    const opt = document.createElement("option");
    opt.value = String(g.id);
    opt.textContent = g.name;
    select.appendChild(opt);
  }
  return await new Promise((resolve) => {
    const cleanup = () => {
      panel.classList.add("hidden");
      document.getElementById("ss14GameOkBtn").onclick = null;
      document.getElementById("ss14GameCancelBtn").onclick = null;
    };
    document.getElementById("ss14GameCancelBtn").onclick = () => {
      cleanup();
      resolve(null);
    };
    document.getElementById("ss14GameOkBtn").onclick = async () => {
      const raw = select.value;
      const gameTitleId = raw === "" ? null : Number(raw);
      cleanup();
      try {
        if (onConfirm) await onConfirm(gameTitleId);
        resolve(gameTitleId);
      } catch (_) {
        resolve(null);
      }
    };
  });
}

async function ss14StartConsoleStation(tile) {
  if (!ss14IsConsoleTile(tile)) {
    await api(`/api/operator/stations/${encodeURIComponent(tile.id)}/start`, { method: "POST" });
    return;
  }
  await ss14ShowGamePicker({
    hint: "شروع کنسول",
    mode: tile.defaultPlayMode || "Offline",
    playerCount: tile.defaultPlayerCount || 1,
    onConfirm: async (gameTitleId) => {
      await api(`/api/operator/stations/${encodeURIComponent(tile.id)}/start`, {
        method: "POST",
        body: JSON.stringify(gameTitleId == null ? {} : { gameTitleId }),
      });
    },
  });
}

function ss14EnsureSessionGameControls() {
  let btn = document.getElementById("ss14GameChangeBtn");
  if (!btn) {
    btn = document.createElement("button");
    btn.type = "button";
    btn.id = "ss14GameChangeBtn";
    // temp text
    btn.textContent = "تغییر بازی";
    const host = document.getElementById("sessionActions");
    const anchor = document.getElementById("playerCountBtn");
    host.insertBefore(btn, anchor);
    btn.onclick = async () => {
      const tile = (lastGridTiles || []).find((x) => x.sessionId === selectedSessionId);
      if (!tile) return;
      const cmd = newCommandId();
      await ss14ShowGamePicker({
        hint: "تغییر بازی",
        mode: tile.playMode || "Offline",
        playerCount: tile.playerCount || 1,
        onConfirm: async (gameTitleId) => {
          await api(`/api/operator/sessions/${selectedSessionId}/game`, {
            method: "POST",
            body: JSON.stringify({
              gameTitleId,
              expectedVersion: tile.rowVersion,
              commandId: cmd,
            }),
          });
          await refreshGrid();
        },
      });
    };
  }
  let label = document.getElementById("ss14CurrentGameLbl");
  if (!label) {
    label = document.createElement("p");
    label.id = "ss14CurrentGameLbl";
    const host = document.getElementById("sessionActions");
    host.insertBefore(label, document.getElementById("sessionLabel").nextSibling);
  }
  return { btn, label };
}

async function ss14FilterPlayerCountSelect(tile) {
  const select = document.getElementById("playerCountSelect");
  if (!select) return;
  const mode = tile?.playMode || "Offline";
  if (!tile?.currentGameTitleId) {
    for (const opt of select.options) {
      opt.disabled = false;
      opt.hidden = false;
    }
    return;
  }
  const rated = tile.currentGameRatedPlayerCounts;
  if (Array.isArray(rated) && rated.length >= 0) {
    for (const opt of select.options) {
      const count = Number(opt.value);
      const ok = rated.length === 0 ? false : rated.includes(count);
      opt.disabled = !ok;
      opt.hidden = !ok;
    }
    return;
  }
  for (const opt of select.options) {
    const count = Number(opt.value);
    const games = await ss14FetchGamesForOption(mode, count).catch(() => []);
    const ok = games.some((g) => g.id === tile.currentGameTitleId);
    opt.disabled = !ok;
    opt.hidden = !ok;
  }
}

function ss14RefreshSessionGameUi(tile, isPcStation) {
  const { btn, label } = ss14EnsureSessionGameControls();
  const show = !isPcStation && (tile.status === "Playing" || tile.status === "Paused");
  btn.classList.toggle("hidden", !show);
  label.classList.toggle("hidden", !show);
  label.textContent = tile.currentGameTitleName
    ? `بازی: ${tile.currentGameTitleName}`
    : "بدون بازی خاص";
}

let ss14TransferConfirmEl = null;

function ss14EnsureTransferConfirm() {
  if (ss14TransferConfirmEl) return ss14TransferConfirmEl;
  const el = document.createElement("div");
  el.id = "ss14TransferConfirm";
  el.className = "panel hidden";
  el.innerHTML =
    '<p>بازی برداشته می‌شود</p><button type="button" id="ss14TransferOkBtn">تأیید</button><button type="button" id="ss14TransferCancelBtn">انصراف</button>';
  document.body.appendChild(el);
  ss14TransferConfirmEl = el;
  return el;
}

function ss14ConfirmTransferIfGame(tile) {
  if (!tile?.currentGameTitleId) return Promise.resolve(true);
  const panel = ss14EnsureTransferConfirm();
  panel.classList.remove("hidden");
  return new Promise((resolve) => {
    const done = (ok) => {
      panel.classList.add("hidden");
      document.getElementById("ss14TransferOkBtn").onclick = null;
      document.getElementById("ss14TransferCancelBtn").onclick = null;
      resolve(ok);
    };
    document.getElementById("ss14TransferCancelBtn").onclick = () => done(false);
    document.getElementById("ss14TransferOkBtn").onclick = () => done(true);
  });
}

function ss14RenderTileGameBadge(el, tile) {
  if (!tile?.currentGameTitleName) return;
  const span = document.createElement("span");
  span.className = "ss14-game-badge";
  span.textContent = tile.currentGameTitleName;
  el.appendChild(span);
}

let ss14MgrTitles = [];
let ss14MgrConsoleKeys = [];
let ss14MgrSelectedId = null;

function ss14FmtToman(n) {
  if (n == null || n === "") return "";
  const num = Number(n);
  if (!Number.isFinite(num)) return String(n);
  return num.toLocaleString("fa-IR");
}

function ss14IsRateDigitChar(ch) {
  return /[0-9\u06F0-\u06F9\u0660-\u0669]/.test(ch);
}

function ss14IsRateSepChar(ch) {
  return ch === "," || ch === "\u060C" || ch === "\u066C";
}

function ss14HasValidThousandsGrouping(trimmed) {
  const groups = [];
  let cur = "";
  for (const ch of trimmed) {
    if (ss14IsRateSepChar(ch)) {
      if (cur.length === 0) return false;
      groups.push(cur);
      cur = "";
    } else if (ss14IsRateDigitChar(ch)) {
      cur += ch;
    } else {
      return false;
    }
  }
  if (cur.length === 0 && groups.length > 0) return false;
  if (cur.length > 0) groups.push(cur);
  if (groups.length === 0) return false;
  for (const g of groups) {
    if (g.length === 0) return false;
  }
  if (groups.length === 1) return true;
  if (groups[0].length < 1 || groups[0].length > 3) return false;
  for (let i = 1; i < groups.length; i++) {
    if (groups[i].length !== 3) return false;
  }
  return true;
}

function ss14RateDigitsNoIllegalLeadingZero(trimmed) {
  let digits = "";
  const fa = "۰۱۲۳۴۵۶۷۸۹";
  for (const ch of trimmed) {
    if (ss14IsRateSepChar(ch)) continue;
    if (!ss14IsRateDigitChar(ch)) continue;
    if (ch >= "0" && ch <= "9") digits += ch;
    else if (ch >= "\u06F0" && ch <= "\u06F9") digits += String(fa.indexOf(ch));
    else digits += String("0123456789"[ch.charCodeAt(0) - 0x0660]);
  }
  return !(digits.length > 1 && digits[0] === "0");
}

function ss14IsAllowedRateInput(raw) {
  if (raw == null) return false;
  const trimmed = String(raw).trim();
  if (trimmed === "") return false;
  if (!/^[0-9\u06F0-\u06F9\u0660-\u0669,\u060C\u066C]+$/.test(trimmed)) return false;
  if (!ss14HasValidThousandsGrouping(trimmed)) return false;
  return ss14RateDigitsNoIllegalLeadingZero(trimmed);
}

function ss14ParseTomanInput(raw) {
  if (raw == null) return null;
  const trimmed = String(raw).trim();
  if (trimmed === "" || !ss14IsAllowedRateInput(trimmed)) return null;
  const fa = "۰۱۲۳۴۵۶۷۸۹";
  const s = trimmed
    .replace(/[,\u060C\u066C]/g, "")
    .replace(/[\u06F0-\u06F9]/g, (d) => String(fa.indexOf(d)))
    .replace(/[\u0660-\u0669]/g, (d) => String("0123456789"[d.charCodeAt(0) - 0x0660]));
  if (!s || !/^[0-9]+$/.test(s)) return null;
  if (s.length > 1 && s[0] === "0") return null;
  const n = Number(s);
  return Number.isFinite(n) && n > 0 ? n : null;
}

function ss14OptionLabel(mode, playerCount) {
  if (String(mode).toLowerCase() === "online") return "آنلاین";
  const p = playerCount ?? 1;
  return `آفلاین ${p.toLocaleString("fa-IR")} دسته`;
}

function ss14EnsureManagerPanelStyles() {
  if (document.getElementById("ss14GameRatesStyle")) return;
  const style = document.createElement("style");
  style.id = "ss14GameRatesStyle";
  style.textContent =
    "#ss14GameRatesPanel{max-width:100%;overflow-x:hidden;box-sizing:border-box}" +
    "#ss14GameRatesPanel #ss14GameEditForm{display:flex;flex-direction:column;gap:0.5rem;max-width:100%}" +
    "#ss14GameRatesPanel #ss14GameRatesTable{width:100%;max-width:100%;border-collapse:collapse}" +
    "#ss14GameRatesPanel #ss14GameRatesTable thead{display:none}" +
    "#ss14GameRatesPanel #ss14GameRatesTable tr{display:block;border:1px solid #ccc;margin:0.5rem 0;padding:0.5rem;box-sizing:border-box}" +
    "#ss14GameRatesPanel #ss14GameRatesTable td{display:block;width:100%;max-width:100%;box-sizing:border-box;padding:0.25rem 0}" +
    "#ss14GameRatesPanel #ss14GameRatesTable td.ss14-rate-cell label{display:block;font-size:0.85rem;line-height:1.35;margin:0 0 0.25rem;word-wrap:break-word;overflow-wrap:anywhere;max-width:100%;min-width:1px;min-height:1em}" +
    "#ss14GameRatesPanel #ss14GameRatesTable input[type=\"text\"]{width:100%;max-width:100%;box-sizing:border-box}" +
    "#ss14GameRatesPanel .ss14-game-rates-error{display:block;width:100%;max-width:100%;margin:0.35rem 0 0;color:#b00020;font-size:0.9rem;line-height:1.35;word-wrap:break-word;overflow-wrap:anywhere}";
  document.head.appendChild(style);
}

function ss14EnsureManagerPanel() {
  let section = document.getElementById("ss14GameRatesPanel");
  if (section) return section;
  ss14EnsureManagerPanelStyles();
  section = document.createElement("section");
  section.id = "ss14GameRatesPanel";
  section.className = "panel";
  // temp text
  section.innerHTML =
    '<h2>نرخ بازی</h2>' +
    '<ul id="ss14GameTitleList"></ul>' +
    // temp text
    '<button type="button" id="ss14GameNewBtn">بازی جدید</button>' +
    '<div id="ss14GameEditForm">' +
    '<label>نام <input id="ss14GameName" type="text" autocomplete="off"></label> ' +
    '<label><input type="checkbox" id="ss14GameEnabled" checked> فعال</label>' +
    '<table id="ss14GameRatesTable"><thead><tr><th>گزینه</th><th>نرخ عادی (تومان)</th><th>نرخ ساعت خاص (تومان)</th><th></th></tr></thead>' +
    '<tbody id="ss14GameRatesBody"></tbody></table>' +
    '<button type="button" id="ss14GameSaveBtn">ذخیره</button> ' +
    '<p id="ss14GameRatesError" class="ss14-game-rates-error" role="alert"></p> ' +
    '<button type="button" id="ss14GameDeleteBtn">حذف</button>' +
    "</div>";
  document.getElementById("cafeSettings").parentElement.insertBefore(section, document.getElementById("cafeSettings"));
  document.getElementById("ss14GameNewBtn").onclick = () => {
    ss14MgrSelectedId = null;
    ss14RenderManagerForm(null);
  };
  document.getElementById("ss14GameSaveBtn").onclick = () => ss14SaveManagerTitle();
  document.getElementById("ss14GameDeleteBtn").onclick = () => ss14DeleteManagerTitle();
  return section;
}

function ss14RenderManagerTitleList() {
  const list = document.getElementById("ss14GameTitleList");
  if (!list) return;
  list.replaceChildren();
  for (const t of ss14MgrTitles) {
    const li = document.createElement("li");
    const btn = document.createElement("button");
    btn.type = "button";
    btn.textContent = `${t.name}${t.enabled ? "" : " (غیرفعال)"}`;
    btn.onclick = () => {
      ss14MgrSelectedId = t.id;
      ss14RenderManagerForm(t);
    };
    if (ss14MgrSelectedId === t.id) btn.setAttribute("aria-current", "true");
    li.appendChild(btn);
    list.appendChild(li);
  }
}

function ss14RenderManagerForm(title) {
  const nameEl = document.getElementById("ss14GameName");
  const enEl = document.getElementById("ss14GameEnabled");
  const body = document.getElementById("ss14GameRatesBody");
  const delBtn = document.getElementById("ss14GameDeleteBtn");
  if (!body) return;
  nameEl.value = title?.name ?? "";
  enEl.checked = title?.enabled !== false;
  delBtn.classList.toggle("hidden", !title?.id);
  const ratesByKey = new Map();
  for (const r of title?.rates ?? []) {
    const key = `${r.mode}:${r.playerCount ?? "-"}`;
    ratesByKey.set(key, r);
  }
  body.replaceChildren();
  for (const k of ss14MgrConsoleKeys) {
    const key = `${k.mode}:${k.playerCount ?? "-"}`;
    const row = ratesByKey.get(key);
    const tr = document.createElement("tr");
    tr.dataset.mode = k.mode;
    tr.dataset.playerCount = k.playerCount ?? "";
    const tdLabel = document.createElement("td");
    tdLabel.textContent = ss14OptionLabel(k.mode, k.playerCount);
    const safeKey = key.replace(/:/g, "-");
    const lblHourlyText = "نرخ عادی (تومان در ساعت)";
    const lblNightText = "نرخ ساعت خاص (تومان در ساعت)";
    const idH = `ss14RateHourly-${safeKey}`;
    const idN = `ss14RateNight-${safeKey}`;
    const tdH = document.createElement("td");
    tdH.className = "ss14-rate-cell";
    const lblH = document.createElement("label");
    lblH.htmlFor = idH;
    lblH.textContent = lblHourlyText;
    const inH = document.createElement("input");
    inH.id = idH;
    inH.type = "text";
    inH.inputMode = "numeric";
    inH.dataset.rate = "hourly";
    inH.setAttribute("aria-label", lblHourlyText);
    inH.value = row?.hourlyToman != null ? ss14FmtToman(row.hourlyToman) : "";
    tdH.append(lblH, inH);
    const tdN = document.createElement("td");
    tdN.className = "ss14-rate-cell";
    const lblN = document.createElement("label");
    lblN.htmlFor = idN;
    lblN.textContent = lblNightText;
    const inN = document.createElement("input");
    inN.id = idN;
    inN.type = "text";
    inN.inputMode = "numeric";
    inN.dataset.rate = "night";
    inN.setAttribute("aria-label", lblNightText);
    inN.value = row?.nightHourlyToman != null ? ss14FmtToman(row.nightHourlyToman) : "";
    tdN.append(lblN, inN);
    const tdNote = document.createElement("td");
    /* temp text */
    if (title?.id && row?.hourlyToman == null) {
      tdNote.textContent = "در فهرست شروع نمی‌آید";
    }
    tr.append(tdLabel, tdH, tdN, tdNote);
    body.appendChild(tr);
  }
}

async function ss14LoadManagerData() {
  ss14MgrTitles = (await api("/api/operator/game-titles", { background: true })) || [];
  ss14MgrConsoleKeys = (await api("/api/operator/game-titles/console-options", { background: true })) || [];
  if (!ss14MgrConsoleKeys.length && ss14MgrTitles[0]?.rates?.length) {
    ss14MgrConsoleKeys = ss14MgrTitles[0].rates.map((r) => ({
      mode: r.mode,
      playerCount: r.playerCount,
    }));
  }
  if (ss14MgrSelectedId != null && !ss14MgrTitles.some((t) => t.id === ss14MgrSelectedId)) {
    ss14MgrSelectedId = ss14MgrTitles[0]?.id ?? null;
  }
  if (ss14MgrSelectedId == null && ss14MgrTitles.length) {
    ss14MgrSelectedId = ss14MgrTitles[0].id;
  }
  ss14RenderManagerTitleList();
  const sel = ss14MgrTitles.find((t) => t.id === ss14MgrSelectedId);
  ss14RenderManagerForm(ss14MgrSelectedId == null ? null : sel ?? null);
}

function ss14CollectManagerRates() {
  const rows = [];
  const body = document.getElementById("ss14GameRatesBody");
  if (!body) return rows;
  for (const tr of body.querySelectorAll("tr")) {
    const hourlyRaw = tr.querySelector('input[data-rate="hourly"]')?.value;
    const hourlyTrim = hourlyRaw != null ? String(hourlyRaw).trim() : "";
    if (hourlyTrim === "") continue;
    const hourly = ss14ParseTomanInput(hourlyTrim);
    if (hourly == null) {
      throw new Error("مقدار نرخ عادی نامعتبر است.");
    }
    const nightRaw = tr.querySelector('input[data-rate="night"]')?.value;
    const nightTrim = nightRaw != null ? String(nightRaw).trim() : "";
    const night = nightTrim === "" ? null : ss14ParseTomanInput(nightTrim);
    if (nightTrim !== "" && night == null) {
      throw new Error("مقدار نرخ ساعت خاص نامعتبر است.");
    }
    if (hourly <= 0) {
      throw new Error("نرخ عادی باید بیشتر از صفر باشد.");
    }
    if (night != null && night <= 0) {
      throw new Error("نرخ ساعت خاص باید خالی یا بیشتر از صفر باشد.");
    }
    const mode = tr.dataset.mode;
    const pc = tr.dataset.playerCount === "" ? null : Number(tr.dataset.playerCount);
    rows.push({
      mode,
      playerCount: String(mode).toLowerCase() === "online" ? null : pc,
      hourlyToman: hourly,
      nightHourlyToman: night,
    });
  }
  return rows;
}

function ss14ShowManagerError(msg) {
  const el = document.getElementById("ss14GameRatesError");
  if (el) el.textContent = msg || "";
}

async function ss14SaveManagerTitle() {
  ss14ShowManagerError("");
  try {
    const name = document.getElementById("ss14GameName")?.value?.trim();
    if (!name) {
      ss14ShowManagerError("نام بازی الزامی است.");
      return;
    }
    const enabled = !!document.getElementById("ss14GameEnabled")?.checked;
    const rates = ss14CollectManagerRates();
    const body = { name, enabled, rates };
    const saveOpts = { method: "POST", body: JSON.stringify(body), background: true };
    if (ss14MgrSelectedId == null) {
      const created = await api("/api/operator/game-titles", saveOpts);
      ss14MgrSelectedId = created.id;
    } else {
      await api(`/api/operator/game-titles/${ss14MgrSelectedId}`, { ...saveOpts, method: "PUT" });
    }
    await ss14LoadManagerData();
    ss14ShowManagerError("");
  } catch (e) {
    ss14ShowManagerError(String(e.message || e));
  }
}

async function ss14DeleteManagerTitle() {
  if (ss14MgrSelectedId == null) return;
  try {
    await api(`/api/operator/game-titles/${ss14MgrSelectedId}`, { method: "DELETE" });
    ss14MgrSelectedId = null;
    await ss14LoadManagerData();
  } catch (_) {
    /* api() shows Persian error */
  }
}

async function ss14RefreshManagerGameUi(isMgr) {
  const section = ss14EnsureManagerPanel();
  section.classList.toggle("hidden", !isMgr);
  if (isMgr) {
    try {
      await ss14LoadManagerData();
    } catch (_) {
      /* errors via showError */
    }
  }
}
// --- end SS-14 ---

// --- K-OP (MVP installer B) ---
function kioskOpFormatElapsed(startedAtUtc) {
  const start = new Date(startedAtUtc).getTime();
  const sec = Math.max(0, Math.floor((Date.now() - start) / 1000));
  return kioskOpFormatDurationSeconds(sec);
}

function kioskOpFormatElapsedBetween(startedAtUtc, endUtc) {
  const start = new Date(startedAtUtc).getTime();
  const end = new Date(endUtc).getTime();
  const sec = Math.max(0, Math.floor((end - start) / 1000));
  return kioskOpFormatDurationSeconds(sec);
}

function kioskOpFormatDurationSeconds(totalSec) {
  const m = Math.floor(totalSec / 60);
  const h = Math.floor(m / 60);
  const mm = String(m % 60).padStart(2, "0");
  const hh = String(h).padStart(2, "0");
  return `${hh}:${mm}`;
}

function kioskOpFormatDurationPersian(totalSec) {
  if (totalSec < 60) return "کمتر از ۱ دقیقه";
  const m = Math.floor(totalSec / 60);
  const h = Math.floor(m / 60);
  const rem = m % 60;
  if (h > 0) return `${h} ساعت و ${rem} دقیقه`;
  return `${m} دقیقه`;
}

function kioskOpFreeUseReasonLabel(r) {
  if (r.endedAtUtc) {
    if (r.endReason === "manual") return "دستی";
    if (r.endReason === "auto_idle") return "خودکار";
    if (r.endReason === "restart") return "خاموش/ری‌استارت";
    return r.endReason || "—";
  }
  if (r.noContact) return "بی‌ارتباط";
  return "در جریان";
}

function kioskOpRenderFreeUseReport(body) {
  const note =
    '<p class="muted">این استفاده‌ها رایگان‌اند و در درآمد و جمع دریافتی حساب نمی‌شوند.</p>';
  const totalDur = kioskOpFormatDurationPersian(body.totalDurationSeconds || 0);
  let summary = `<p>${body.count} مورد — مجموع مدت: ${totalDur}</p>`;
  if (body.openLiveCount > 0) {
    summary += `<p class="muted">(${body.openLiveCount} مورد هنوز باز است و تا همین حالا حساب شده است.)</p>`;
  }
  if (body.noContactCount > 0) {
    summary += `<p class="muted">(${body.noContactCount} مورد بی‌ارتباط است و تا آخرین ارتباط حساب شده است.)</p>`;
  }
  const restartNote = body.hasRestartEnd
    ? '<p class="muted">برای «خاموش/ری‌استارت»، زمان پایان آخرین زمان فعالیت ثبت‌شدهٔ همان سیستم است.</p>'
    : "";
  const noContactNote =
    (body.noContactCount || 0) > 0
      ? '<p class="muted">برای «بی‌ارتباط»، مدت تا آخرین ارتباط سیستم با سرور حساب شده است.</p>'
      : "";
  const rows = (body.rows || []).map((r) => {
    let duration = kioskOpFormatDurationPersian(r.durationSeconds || 0);
    if (!r.endedAtUtc && r.noContact) duration = `${duration} تا آخرین ارتباط`;
    else if (!r.endedAtUtc) duration = `${duration} تا الان`;
    return [
      r.stationId,
      r.actorRole === "manager" ? "مدیر کیوسک" : "اپراتور کیوسک",
      r.startedAtUtc,
      r.endedAtUtc || "—",
      duration,
      kioskOpFreeUseReasonLabel(r),
    ];
  });
  return (
    `<h3>استفادهٔ رایگان کیوسک</h3>${note}${summary}${restartNote}${noContactNote}` +
    renderReportsTable(
      "استفادهٔ رایگان کیوسک",
      ["PC", "اپراتور کیوسک", "شروع", "پایان", "مدت", "علت پایان"],
      rows.length ? rows : [["—", "—", "—", "—", "—", "—"]]
    )
  );
}

bindClick("freeUseReportBtn", async () => {
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
    `/api/operator/reports/free-use?fromUtc=${encodeURIComponent(range.fromUtc)}&toUtc=${encodeURIComponent(rangeTo.toUtc)}`
  );
  document.getElementById("reportsOut").innerHTML = kioskOpRenderFreeUseReport(body);
});

bindClick("mgrFreeIdleSaveBtn", async () => {
  const raw = (document.getElementById("mgrFreeIdleMinutes").value || "").replace(/[۰-۹]/g, (c) =>
    String("۰۱۲۳۴۵۶۷۸۹".indexOf(c))
  );
  const minutes = Number(raw);
  await api("/api/operator/shift/settings", {
    method: "PUT",
    body: JSON.stringify({ kioskFreeUseIdleMinutes: minutes }),
  });
});
// --- end K-OP ---
