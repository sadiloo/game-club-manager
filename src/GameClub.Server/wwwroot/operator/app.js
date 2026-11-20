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
let lastCollectibleToman = 0;
let cpBgPollFailures = 0;

/** Spec v2.1 §11 — final copy keyed for text-only edits later. */
const CP_TEXT = {
  cp: { retry: "تلاش دوباره" },
  f12: {
    title: "تنظیم تولد",
    percent: "درصد تخفیف تولد",
    windowKind: "بازهٔ تخفیف",
    windowDays: "تعداد روزها",
    anchor: "حساب روزها",
    done: "تنظیم تولد ذخیره شد.",
    kind: {
      birth_month: "کل ماه تولد",
      day_window: "چند روز",
      exact_day: "فقط روز تولد",
    },
    anchorOpt: {
      on_and_after: "از روز تولد به بعد",
      around: "دور روز تولد (نیمی پیش، نیمی پس)",
    },
    errDays: "دست‌کم ۱ روز.",
  },
  f14: {
    title: "جدول سطح‌ها",
    help: "هر مشتری بالاترین سطحی را می‌گیرد که به حداقل ساعت بازی‌اش رسیده است.",
    col: { name: "نام سطح", hours: "حداقل ساعت بازی", percent: "درصد تخفیف" },
    unit: { hours: "ساعت", percent: "٪" },
    rowLabel: "سطح {i}",
    add: "افزودن سطح",
    delete: "حذف",
    save: "ذخیرهٔ جدول",
    saving: "در حال ذخیره…",
    done: "جدول سطح‌ها ذخیره شد.",
    loading: "در حال بارگذاری…",
    loadFail: "جدول سطح‌ها بارگذاری نشد.",
    empty: { title: "هنوز سطحی نیست.", body: "با «افزودن سطح» اولین سطح را بسازید." },
    err: {
      summary: "{n} خانه درست نیست. خانه‌های قرمز را درست کنید.",
      noRows: "دست‌کم یک سطح لازم است.",
      nameEmpty: "نام سطح را وارد کنید.",
      nameDuplicate: "این نام برای سطح دیگری هم آمده است.",
      hoursEmpty: "حداقل ساعت را وارد کنید.",
      hoursOrder: "باید از سطح قبلی ({h} ساعت) بیشتر باشد.",
      percentEmpty: "درصد را وارد کنید.",
      percentRange: "درصد باید بین ۰ و ۱۰۰ باشد.",
      server: "جدول سطح‌ها درست نیست. نام‌ها، ساعت‌ها و درصدها را بررسی کنید.",
      forbidden: "فقط مدیر یا مالک می‌تواند جدول سطح‌ها را عوض کند.",
      saveFail: "جدول سطح‌ها ذخیره نشد. دوباره تلاش کنید.",
    },
  },
};

function cpTextFaDigits(n) {
  return String(n).replace(/\d/g, (d) => "۰۱۲۳۴۵۶۷۸۹"[Number(d)]);
}

function cpTextFormat(template, vars) {
  return template.replace(/\{(\w+)\}/g, (_, k) => (vars[k] != null ? String(vars[k]) : ""));
}

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
  if (typeof cpLayer !== "undefined" && cpLayer.active) {
    if (cpLayer.active.busy && typeof cpLayer.active.setServerError === "function") {
      cpLayer.active.setServerError(typeof cpTranslateApiError === "function" ? cpTranslateApiError(null, msg) : msg);
      return;
    }
  }
  const el = document.getElementById("error");
  el.textContent = msg || "";
}

function setCpBgStatus(failed) {
  const el = document.getElementById("cpBgStatus");
  if (!el) return;
  if (failed) {
    el.textContent = "اتصال به سرور برقرار نیست؛ دوباره تلاش می‌شود…";
    el.classList.remove("hidden");
  } else {
    el.textContent = "";
    el.classList.add("hidden");
  }
}

function paymentMethodFa(method) {
  const map = { Cash: "نقد", CardReader: "کارتخوان", CardToCard: "کارت‌به‌کارت", Wallet: "کیف پول" };
  return map[method] || method;
}

function walletMethodFa(method) {
  return paymentMethodFa(method);
}

function paymentRemainingToman() {
  const paid = paymentDraft.reduce((s, l) => s + (l.amountToman || 0), 0);
  return Math.max(0, (lastCollectibleToman || 0) - paid);
}

function fmtTehranDateTime(iso) {
  if (!iso) return "—";
  return new Date(iso).toLocaleString("fa-IR", {
    timeZone: "Asia/Tehran",
    dateStyle: "short",
    timeStyle: "short",
  });
}

function filterTransferDestinations(sourceTile) {
  const srcId = sourceTile?.id;
  const gk = sourceTile?.groupKind ?? null;
  return (lastGridTiles || []).filter(
    (t) =>
      t.status === "Free" &&
      t.id !== srcId &&
      (gk == null || t.groupKind === gk) &&
      t.id,
  );
}

const CP_JALALI_MONTHS = [
  "فروردین",
  "اردیبهشت",
  "خرداد",
  "تیر",
  "مرداد",
  "شهریور",
  "مهر",
  "آبان",
  "آذر",
  "دی",
  "بهمن",
  "اسفند",
];

function cpIsJalaliLeapYear(year) {
  const breaks = [1, 5, 9, 13, 17, 22, 26, 30];
  const gy = year + 621;
  const leap = -14 + Math.floor((gy + 1) * 0.2422);
  const jp = breaks[(year + 12) % 33];
  return leap === jp;
}

function cpJalaliMonthLength(month, year) {
  if (month >= 1 && month <= 6) return 31;
  if (month >= 7 && month <= 11) return 30;
  return cpIsJalaliLeapYear(year) ? 30 : 29;
}

function validateJalaliBirth(year, month, day) {
  const curJy = new Date().toLocaleDateString("fa-IR-u-ca-persian", { year: "numeric" }).replace(/[^\d]/g, "");
  const maxY = Number(curJy) || 1500;
  if (year < 1300 || year > maxY || month < 1 || month > 12) return "تاریخ درست نیست.";
  const maxDay = cpJalaliMonthLength(month, year);
  if (day < 1 || day > maxDay) return "تاریخ درست نیست.";
  return null;
}

function snapshotLevelLadderFromDom(host) {
  const rows = [];
  host.querySelectorAll(".cp-ladder-card").forEach((card) => {
    rows.push({
      name: card.querySelector('[data-ladder="name"]')?.value ?? "",
      minimumLifetimeHours: card.querySelector('[data-ladder="hours"]')?.value ?? "",
      discountPercent: card.querySelector('[data-ladder="pct"]')?.value ?? "",
    });
  });
  return rows;
}

/** Mirrors GameClub.Billing/Levels.cs LevelLadder.Create :26-65. */
function validateLevelLadderFieldErrors(rowDrafts) {
  const fieldErrors = {};
  let count = 0;
  const mark = (key, msg) => {
    if (!fieldErrors[key]) {
      fieldErrors[key] = msg;
      count++;
    }
  };
  if (!Array.isArray(rowDrafts) || !rowDrafts.length) {
    return { fieldErrors: {}, count: 1, rows: [], formOnly: CP_TEXT.f14.err.noRows };
  }
  const names = new Map();
  let previousHours = null;
  const rows = [];
  for (let i = 0; i < rowDrafts.length; i++) {
    const draft = rowDrafts[i];
    const prefix = `ladder:${i}`;
    const nameRaw = String(draft.name ?? "");
    const name = nameRaw.trim();
    const hoursTrim = String(draft.minimumLifetimeHours ?? "").trim();
    const pctTrim = String(draft.discountPercent ?? "").trim();

    if (!name) mark(`${prefix}:name`, CP_TEXT.f14.err.nameEmpty);
    else {
      if (!names.has(name)) names.set(name, []);
      names.get(name).push(`${prefix}:name`);
    }

    let hours = null;
    if (!hoursTrim) mark(`${prefix}:hours`, CP_TEXT.f14.err.hoursEmpty);
    else if (typeof cpParseIntegerResult === "function") {
      const hp = cpParseIntegerResult(hoursTrim);
      if (!hp.ok) {
        if (hp.reason === "too_long") mark(`${prefix}:hours`, "عدد نباید بیشتر از ۱۵ رقم باشد.");
        else mark(`${prefix}:hours`, "عدد درست نیست؛ فقط رقم بنویسید.");
      } else {
        hours = hp.value;
        if (hours < 0) mark(`${prefix}:hours`, CP_TEXT.f14.err.hoursEmpty);
        else if (previousHours != null && hours <= previousHours) {
          mark(
            `${prefix}:hours`,
            cpTextFormat(CP_TEXT.f14.err.hoursOrder, { h: cpTextFaDigits(previousHours) }),
          );
        }
      }
    } else {
      hours = cpParseInteger(hoursTrim);
      if (hours == null) mark(`${prefix}:hours`, "عدد درست نیست؛ فقط رقم بنویسید.");
      else if (hours < 0) mark(`${prefix}:hours`, CP_TEXT.f14.err.hoursEmpty);
      else if (previousHours != null && hours <= previousHours) {
        mark(
          `${prefix}:hours`,
          cpTextFormat(CP_TEXT.f14.err.hoursOrder, { h: cpTextFaDigits(previousHours) }),
        );
      }
    }

    let pct = null;
    if (!pctTrim) mark(`${prefix}:percent`, CP_TEXT.f14.err.percentEmpty);
    else if (typeof cpParseIntegerResult === "function") {
      const pp = cpParseIntegerResult(pctTrim);
      if (!pp.ok) {
        if (pp.reason === "too_long") mark(`${prefix}:percent`, "عدد نباید بیشتر از ۱۵ رقم باشد.");
        else mark(`${prefix}:percent`, "عدد درست نیست؛ فقط رقم بنویسید.");
      } else {
        pct = pp.value;
        if (pct < 0 || pct > 100) mark(`${prefix}:percent`, CP_TEXT.f14.err.percentRange);
      }
    } else {
      pct = cpParseInteger(pctTrim);
      if (pct == null) mark(`${prefix}:percent`, "عدد درست نیست؛ فقط رقم بنویسید.");
      else if (pct < 0 || pct > 100) mark(`${prefix}:percent`, CP_TEXT.f14.err.percentRange);
    }

    if (hours != null) previousHours = hours;
    rows.push({
      name,
      minimumLifetimeHours: hours,
      discountPercent: pct,
    });
  }
  for (const indices of names.values()) {
    if (indices.length > 1) {
      for (const key of indices) mark(key, CP_TEXT.f14.err.nameDuplicate);
    }
  }
  return { fieldErrors, count, rows };
}

function clearLevelLadderFieldErrors(host) {
  host.querySelectorAll("[data-cp-ladder-err]").forEach((el) => {
    el.textContent = "";
    el.classList.add("hidden");
  });
  host.querySelectorAll("[data-cp-ladder-field]").forEach((el) => el.removeAttribute("aria-invalid"));
}

function mountLevelLadderLoading(host, session) {
  host.classList.add("cp-ladder-host");
  session.fieldEls.ladder = host;
  host.replaceChildren();
  const p = document.createElement("p");
  p.className = "cp-ladder-loading";
  p.textContent = CP_TEXT.f14.loading;
  host.appendChild(p);
  session._ladderPhase = "loading";
  const submit = document.querySelector("#cpLayer [data-cp-role='submit']");
  if (submit) submit.disabled = true;
}

function mountLevelLadderLoadError(host, session, reload) {
  host.replaceChildren();
  const err = document.createElement("p");
  err.className = "cp-server-error";
  err.textContent = CP_TEXT.f14.loadFail;
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "cp-btn cp-secondary";
  btn.textContent = CP_TEXT.cp.retry;
  btn.onclick = () => reload();
  host.append(err, btn);
  session._ladderPhase = "error";
}

function renderLevelLadderEditor(host, session, rowDrafts) {
  host.classList.add("cp-ladder-host");
  session.fieldEls.ladder = host;
  session.fieldEls.ladder_summary = host;
  session._ladderPhase = "edit";
  const submit = document.querySelector("#cpLayer [data-cp-role='submit']");
  if (submit) submit.disabled = false;

  const help = document.createElement("p");
  help.className = "cp-help";
  help.textContent = CP_TEXT.f14.help;

  const body = document.createElement("div");
  body.className = "cp-ladder-body";

  const draw = (drafts) => {
    body.replaceChildren();
    clearLevelLadderFieldErrors(host);
    if (!drafts.length) {
      const empty = document.createElement("p");
      empty.className = "cp-ladder-empty";
      empty.textContent = `${CP_TEXT.f14.empty.title} ${CP_TEXT.f14.empty.body}`;
      body.appendChild(empty);
    }
    drafts.forEach((row, idx) => {
      const card = document.createElement("div");
      card.className = "cp-ladder-card";
      card.setAttribute("role", "group");
      card.setAttribute("aria-label", cpTextFormat(CP_TEXT.f14.rowLabel, { i: cpTextFaDigits(idx + 1) }));

      const top = document.createElement("div");
      top.className = "cp-ladder-card-head";
      const lbl = document.createElement("span");
      lbl.textContent = cpTextFormat(CP_TEXT.f14.rowLabel, { i: cpTextFaDigits(idx + 1) });
      const rm = document.createElement("button");
      rm.type = "button";
      rm.textContent = CP_TEXT.f14.delete;
      rm.className = "cp-secondary cp-btn";
      rm.setAttribute(
        "aria-label",
        row.name
          ? cpTextFormat("حذف سطح \u2068{name}\u2069", { name: row.name })
          : cpTextFormat("حذف سطح {i}", { i: cpTextFaDigits(idx + 1) }),
      );
      rm.onclick = () => {
        const snap = snapshotLevelLadderFromDom(host);
        snap.splice(idx, 1);
        draw(snap);
      };
      top.append(lbl, rm);
      card.appendChild(top);

      const addField = (fieldKey, labelText, input) => {
        const wrap = document.createElement("label");
        wrap.className = "cp-field";
        const span = document.createElement("span");
        span.textContent = labelText;
        wrap.append(span, input);
        const err = document.createElement("span");
        err.className = "cp-field-error hidden";
        err.dataset.cpLadderErr = `ladder:${idx}:${fieldKey}`;
        wrap.appendChild(err);
        card.appendChild(wrap);
      };

      const nameInp = document.createElement("input");
      nameInp.type = "text";
      nameInp.dir = "auto";
      nameInp.dataset.ladder = "name";
      nameInp.dataset.cpLadderField = `ladder:${idx}:name`;
      nameInp.value = row.name ?? "";
      nameInp.oninput = () => {
        const e = host.querySelector(`[data-cp-ladder-err="ladder:${idx}:name"]`);
        if (e) {
          e.classList.add("hidden");
          e.textContent = "";
        }
        nameInp.removeAttribute("aria-invalid");
      };
      addField("name", CP_TEXT.f14.col.name, nameInp);

      const hoursInp = document.createElement("input");
      hoursInp.type = "text";
      hoursInp.inputMode = "numeric";
      hoursInp.dir = "ltr";
      hoursInp.dataset.ladder = "hours";
      hoursInp.dataset.cpLadderField = `ladder:${idx}:hours`;
      hoursInp.value = row.minimumLifetimeHours ?? "";
      hoursInp.oninput = () => {
        const e = host.querySelector(`[data-cp-ladder-err="ladder:${idx}:hours"]`);
        if (e) {
          e.classList.add("hidden");
          e.textContent = "";
        }
        hoursInp.removeAttribute("aria-invalid");
      };
      addField("hours", CP_TEXT.f14.col.hours, hoursInp);

      const pctInp = document.createElement("input");
      pctInp.type = "text";
      pctInp.inputMode = "numeric";
      pctInp.dir = "ltr";
      pctInp.dataset.ladder = "pct";
      pctInp.dataset.cpLadderField = `ladder:${idx}:percent`;
      pctInp.value = row.discountPercent ?? "";
      pctInp.oninput = () => {
        const e = host.querySelector(`[data-cp-ladder-err="ladder:${idx}:percent"]`);
        if (e) {
          e.classList.add("hidden");
          e.textContent = "";
        }
        pctInp.removeAttribute("aria-invalid");
      };
      addField("percent", CP_TEXT.f14.col.percent, pctInp);

      body.appendChild(card);
    });

    const add = document.createElement("button");
    add.type = "button";
    add.className = "cp-btn cp-secondary";
    add.textContent = CP_TEXT.f14.add;
    add.onclick = () => {
      const snap = snapshotLevelLadderFromDom(host);
      const lastH = snap.length ? cpParseInteger(String(snap[snap.length - 1].minimumLifetimeHours)) : null;
      const nextH = lastH == null ? 0 : lastH + 1;
      snap.push({ name: "", minimumLifetimeHours: String(nextH), discountPercent: "0" });
      draw(snap);
      const lastCard = body.querySelector(".cp-ladder-card:last-of-type");
      lastCard?.querySelector('[data-ladder="name"]')?.focus();
    };
    body.appendChild(add);
  };

  host.replaceChildren(help, body);
  draw(
    rowDrafts.map((r) => ({
      name: r.name ?? "",
      minimumLifetimeHours:
        r.minimumLifetimeHours === undefined || r.minimumLifetimeHours === null
          ? ""
          : String(r.minimumLifetimeHours),
      discountPercent:
        r.discountPercent === undefined || r.discountPercent === null ? "" : String(r.discountPercent),
    })),
  );
  session._ladderRedraw = draw;
}

function readLevelLadderEditor(session) {
  const host = session.fieldEls.ladder || document.querySelector(".cp-ladder-host");
  if (!host) return { errors: { _form: "خطای داخلی." } };
  if (session._ladderPhase === "loading" || session._ladderPhase === "error") {
    return { errors: { _form: CP_TEXT.f14.loadFail } };
  }
  clearLevelLadderFieldErrors(host);
  const drafts = snapshotLevelLadderFromDom(host);
  const { fieldErrors, count, rows, formOnly } = validateLevelLadderFieldErrors(drafts);
  if (count > 0) {
    if (formOnly) return { errors: { _form: formOnly } };
    const summary = cpTextFormat(CP_TEXT.f14.err.summary, { n: cpTextFaDigits(count) });
    for (const [key, msg] of Object.entries(fieldErrors)) {
      const errEl = host.querySelector(`[data-cp-ladder-err="${key}"]`);
      const input = host.querySelector(`[data-cp-ladder-field="${key}"]`);
      if (errEl) {
        errEl.textContent = msg;
        errEl.classList.remove("hidden");
      }
      if (input) input.setAttribute("aria-invalid", "true");
    }
    return { errors: { ...fieldErrors, _form: summary } };
  }
  const payload = rows.map((r) => ({
    name: r.name,
    minimumLifetimeHours: r.minimumLifetimeHours,
    discountPercent: r.discountPercent,
  }));
  return { values: { ladder: payload } };
}

function beginLevelLadderForm(host, session) {
  mountLevelLadderLoading(host, session);
  const reload = () => {
    mountLevelLadderLoading(host, session);
    loadLevelLadderIntoForm(host, session);
  };
  loadLevelLadderIntoForm(host, session, reload);
}

async function loadLevelLadderIntoForm(host, session, onRetry) {
  try {
    const cur = await api("/api/operator/members/settings/level-ladder", { background: true });
    renderLevelLadderEditor(host, session, cur || []);
  } catch (_e) {
    mountLevelLadderLoadError(host, session, onRetry || (() => loadLevelLadderIntoForm(host, session, onRetry)));
  }
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
  let res;
  try {
    res = await fetch(path, {
      credentials: "include",
      headers: { "Content-Type": "application/json", ...(fetchOptions.headers || {}) },
      ...fetchOptions,
    });
  } catch (e) {
    if (isBackground) {
      cpBgPollFailures++;
      setCpBgStatus(true);
    }
    throw e;
  }
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
    if (typeof cpTranslateApiError === "function") {
      let scope = null;
      if (path.includes("level-ladder")) scope = { scope: "level_ladder" };
      else if (path.includes("/void")) scope = { scope: "void_invoice" };
      else if (path.includes("/wallet/gift")) scope = { scope: "member_gift" };
      else if (
        path.includes("/members/settings") ||
        path.includes("/opening-balance") ||
        path.includes("/birthday") ||
        path.includes("/shift/open")
      ) {
        scope = { scope: "manager_action" };
      }
      msg = cpTranslateApiError(code, msg, scope);
    }
    if (isBackground) {
      cpBgPollFailures++;
      setCpBgStatus(true);
    } else {
      showError(msg);
    }
    const err = new Error(msg);
    err.status = res.status;
    err.code = code;
    throw err;
  }
  if (isBackground && cpBgPollFailures > 0) {
    cpBgPollFailures = 0;
    setCpBgStatus(false);
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
    const amount = await cpPrompt({
      title: item.name || "خدمت دلخواه",
      confirmLabel: "افزودن",
      field: {
        kind: "money",
        label: "مبلغ (تومان)",
        placeholder: "۲۰٬۰۰۰",
        positive: true,
      },
    });
    if (amount == null) return;
    body.customAmountToman = amount;
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
  const w = await api(`/api/operator/members/customers/${linked.id}/wallet`, { background: true });
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

function walletTopUpTitle(method) {
  if (method === "Cash") return "شارژ نقدی کیف پول";
  if (method === "CardReader") return "شارژ کارتخوان";
  return "شارژ کارت‌به‌کارت";
}

async function walletTopUp(method) {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) {
    showError("عضوی پیوسته نیست.");
    return;
  }
  const fields = [
    { name: "amountToman", kind: "money", label: "مبلغ شارژ (تومان)", positive: true, placeholder: "۵۰٬۰۰۰" },
    {
      name: "giftToman",
      kind: "money",
      label: "اعتبار هدیه (تومان)",
      required: false,
      allowZero: true,
      positive: false,
      help: "خالی = ۰",
    },
  ];
  if (method === "CardToCard") {
    fields.push({
      name: "destinationCardId",
      kind: "select",
      label: "کارت مقصد",
      value: destinationCards[0]?.id,
      options: destinationCards.map((c) => ({ value: c.id, label: c.id })),
    });
    fields.push({ name: "reference", kind: "text", label: "ارجاع", required: false });
  }
  const w = await api(`/api/operator/members/customers/${linked.id}/wallet`, { background: true }).catch(() => null);
  const bodyText = w
    ? `کیف پول: واقعی ${w.realToman} · هدیه ${w.giftToman} · کل ${w.spendableToman}`
    : linked.name;
  const values = await cpForm({
    title: walletTopUpTitle(method),
    body: bodyText,
    confirmLabel: "ثبت شارژ",
    fields,
    validate: (v) => {
      const errs = {};
      if (!v.amountToman || v.amountToman <= 0) errs.amountToman = "مبلغ نامعتبر است.";
      const gift = v.giftToman == null ? 0 : v.giftToman;
      if (gift < 0) errs.giftToman = "هدیه نامعتبر است.";
      return Object.keys(errs).length ? errs : null;
    },
    onSubmit: async (v) => {
      const amountToman = v.amountToman;
      const giftToman = v.giftToman == null ? 0 : v.giftToman;
      const destinationCardId = v.destinationCardId;
      if (method === "CardToCard" && !destinationCardId) {
        const err = new Error("کارت مقصد الزامی است.");
        throw err;
      }
      const draftKey = buildWalletTopUpDraftKey(linked.id, method, amountToman, giftToman, destinationCardId);
      ensureWalletTopUpCommandIdForDraft(draftKey);
      const commandId = method === "CardToCard"
        ? (walletC2cCommandId ||= walletTopUpCommandId)
        : walletTopUpCommandId;
      const body = { method, amountToman, giftToman, commandId };
      if (method === "CardToCard") {
        body.destinationCardId = destinationCardId;
        if (v.reference) body.reference = v.reference;
      }
      if (walletTopUpInFlight) return;
      walletTopUpInFlight = true;
      setWalletTopUpButtonsDisabled(true);
      try {
        await api(`/api/operator/members/customers/${linked.id}/wallet/top-up`, {
          method: "POST",
          body: JSON.stringify(body),
        });
        renderCustomerChip(linked);
        resetWalletTopUpForm();
        if (method === "CardToCard") {
          cpToast({ tone: "success", text: "شارژ کارت‌به‌کارت ثبت شد و منتظر تأیید است." });
        } else {
          cpToast({ tone: "success", text: "شارژ ثبت شد." });
        }
      } finally {
        walletTopUpInFlight = false;
        setWalletTopUpButtonsDisabled(false);
      }
    },
  });
  if (values == null) resetWalletTopUpForm();
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
    cpToast({ tone: "info", text: "شارژ قابل اصلاحی نیست." });
    return;
  }
  const pickItems = [...list]
    .sort((a, b) => String(b.atUtc).localeCompare(String(a.atUtc)))
    .map((t) => ({
      id: t.topUpGroupId,
      title: `${walletMethodFa(t.method)} · ${t.currentAmountToman} تومان`,
      subtitle: `${fmtTehranDateTime(t.atUtc)} · هدیه ${t.currentGiftToman ?? 0}`,
      row: t,
    }));
  const picked = await cpPick({
    title: "اصلاح شارژ",
    body: "شارژی را که می‌خواهید اصلاح کنید انتخاب کنید.",
    items: pickItems,
    emptyText: "شارژ قابل اصلاحی نیست.",
  });
  if (!picked) {
    resetWalletCorrectForm();
    return;
  }
  const row = picked.row || list.find((x) => x.topUpGroupId === picked.id);
  if (!row) {
    resetWalletCorrectForm();
    return;
  }
  const values = await cpForm({
    title: "اصلاح شارژ",
    body: `${walletMethodFa(row.method)} · ${row.currentAmountToman} تومان + هدیه ${row.currentGiftToman ?? 0}`,
    confirmLabel: "ثبت اصلاح",
    fields: [
      {
        name: "newAmountToman",
        kind: "money",
        label: "مبلغ پرداختی درست (تومان)",
        value: row.currentAmountToman,
        allowZero: true,
        positive: false,
      },
      {
        name: "newGiftToman",
        kind: "money",
        label: "هدیه درست (تومان)",
        value: row.currentGiftToman ?? 0,
        allowZero: true,
        positive: false,
      },
      { name: "description", kind: "textarea", label: "توضیح", value: "" },
    ],
    validate: (v) => {
      const errs = {};
      if (!v.description) errs.description = "توضیح الزامی است.";
      if (
        v.newAmountToman === row.currentAmountToman &&
        (v.newGiftToman ?? 0) === (row.currentGiftToman ?? 0)
      ) {
        errs._form = "تغییری نسبت به مقدار فعلی ندارد.";
      }
      return Object.keys(errs).length ? errs : null;
    },
    onSubmit: async (v) => {
      const newAmountToman = v.newAmountToman ?? 0;
      const newGiftToman = v.newGiftToman ?? 0;
      const description = v.description.trim();
      const draftKey = JSON.stringify({
        topUpGroupId: row.topUpGroupId,
        newAmountToman,
        newGiftToman,
        description,
      });
      ensureWalletCorrectCommandIdForDraft(draftKey);
      await api(`/api/operator/members/wallet/top-ups/${row.topUpGroupId}/correct`, {
        method: "POST",
        body: JSON.stringify({
          newAmountToman,
          newGiftToman,
          description,
          commandId: walletCorrectCommandId,
        }),
      });
      renderCustomerChip(linked);
      cpToast({ tone: "success", text: "اصلاح ثبت شد." });
      resetWalletCorrectForm();
    },
  });
  if (values == null) resetWalletCorrectForm();
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
    el.appendChild(document.createTextNode(t.status));
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
  const key = pendingRowKey(kind, rowId);
  button.dataset.pendingKey = key;
  button.onclick = async () => {
    if (button.disabled) return;
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
      for (const b of document.querySelectorAll(`[data-pending-key="${CSS.escape(key)}"]`)) {
        if (b.isConnected) b.disabled = false;
      }
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
  lastCollectibleToman = bill.collectibleToman ?? 0;
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
    row.textContent = `${paymentMethodFa(line.method)} ${line.amountToman}`;
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
  const destinations = filterTransferDestinations(tile);
  const fields = [
    {
      name: "targetStationId",
      kind: "radio",
      label: "ایستگاه مقصد",
      options: destinations.map((d) => ({ value: d.id, label: `${d.name || d.id} (${d.id})` })),
    },
  ];
  if (!document.getElementById("transferFaultWrap").classList.contains("hidden")) {
    fields.push({
      name: "markSourceBroken",
      kind: "checkbox",
      label: "ایستگاه مبدأ خراب است",
      value: document.getElementById("transferFaultChk").checked,
    });
  }
  const values = await cpForm({
    title: "پرش ایستگاه",
    body: tile ? `از ${tile.id}` : undefined,
    warning: tile?.currentGameTitleId
      ? `بازی فعلی (${tile.currentGameTitleName || tile.currentGameTitleId}) از جلسه برداشته می‌شود.`
      : undefined,
    confirmLabel: "جابه‌جا کن",
    fields,
    validate: (v) => {
      if (!v.targetStationId) return { _form: destinations.length ? "ایستگاه مقصد را انتخاب کنید." : "ایستگاه آزادی از همین نوع نیست." };
      return null;
    },
    onSubmit: async (v) => {
      const markSourceBroken = !!v.markSourceBroken;
      document.getElementById("transferFaultChk").checked = markSourceBroken;
      const commandId = newCommandId();
      try {
        await api(`/api/operator/sessions/${selectedSessionId}/transfer`, {
          method: "POST",
          body: JSON.stringify({
            targetStationId: String(v.targetStationId).trim(),
            markSourceBroken,
            expectedVersion: tile?.rowVersion ?? null,
            commandId,
          }),
        });
        document.getElementById("sessionActions").classList.add("hidden");
      } finally {
        await refreshGrid();
      }
    },
  });
  if (values == null) return;
};

document.getElementById("pauseBtn").onclick = async () => {
  await api(`/api/operator/sessions/${selectedSessionId}/pause`, { method: "POST" });
  await refreshGrid();
};

document.getElementById("forceLogoutBtn").onclick = async () => {
  if (!selectedSessionId) return;
  const tile = (lastGridTiles || []).find((x) => x.sessionId === selectedSessionId);
  const ok = await cpConfirm({
    title: `خروج اجباری ${tile?.id || ""}؟`,
    body: "جلسهٔ این ایستگاه همین حالا متوقف می‌شود.",
    confirmLabel: "خروج اجباری",
    tone: "danger",
    focusCancelFirst: true,
    onConfirm: async () => {
      await api(`/api/operator/sessions/${selectedSessionId}/force-logout`, { method: "POST" });
      document.getElementById("sessionActions").classList.add("hidden");
      await refreshGrid();
    },
  });
  if (!ok) return;
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

async function addPaymentLineSimple(method, title, label) {
  const remain = paymentRemainingToman();
  const amount = await cpPrompt({
    title,
    confirmLabel: "افزودن",
    body: remain > 0 ? `باقی‌مانده: ${remain.toLocaleString("fa-IR")} تومان` : undefined,
    field: {
      kind: "money",
      label,
      value: remain > 0 ? remain : undefined,
      positive: true,
    },
    validate: null,
  });
  if (amount == null) return;
  if (!Number.isInteger(amount) || amount <= 0) {
    cpToast({ tone: "error", text: "مبلغ باید عدد صحیح مثبت باشد." });
    return;
  }
  paymentDraft.push({ method, amountToman: amount });
  renderPaymentLines();
}

document.getElementById("addCashLineBtn").onclick = () => {
  addPaymentLineSimple("Cash", "پرداخت نقد", "مبلغ نقد (تومان)").catch(() => {});
};

document.getElementById("addCardLineBtn").onclick = () => {
  addPaymentLineSimple("CardReader", "پرداخت کارتخوان", "مبلغ کارتخوان (تومان)").catch(() => {});
};

document.getElementById("addC2cLineBtn").onclick = async () => {
  const remain = paymentRemainingToman();
  const values = await cpForm({
    title: "پرداخت کارت‌به‌کارت",
    body: remain > 0 ? `باقی‌مانده: ${remain.toLocaleString("fa-IR")} تومان` : undefined,
    confirmLabel: "افزودن",
    fields: [
      {
        name: "amountToman",
        kind: "money",
        label: "مبلغ کارت‌به‌کارت (تومان)",
        value: remain > 0 ? remain : undefined,
        positive: true,
      },
      {
        name: "destinationCardId",
        kind: "select",
        label: "کارت مقصد",
        required: false,
        value: destinationCards[0]?.id,
        options: destinationCards.map((c) => ({ value: c.id, label: c.id })),
      },
      { name: "reference", kind: "text", label: "ارجاع", required: false },
    ],
    validate: (v) => {
      if (!v.amountToman || v.amountToman <= 0) return { amountToman: "مبلغ باید عدد صحیح مثبت باشد." };
      return null;
    },
  });
  if (values == null) return;
  paymentDraft.push({
    method: "CardToCard",
    amountToman: values.amountToman,
    destinationCardId: values.destinationCardId || undefined,
    reference: values.reference || undefined,
  });
  renderPaymentLines();
};

document.getElementById("addWalletLineBtn").onclick = async () => {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) {
    showError("برای پرداخت کیف پول، عضو را پیوند دهید.");
    return;
  }
  renderCustomerChip(linked);
  await addPaymentLineSimple("Wallet", "پرداخت کیف پول", "مبلغ کیف پول (تومان)");
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
  if (!n) {
    showError("شمارهٔ فاکتور را وارد کنید.");
    return;
  }
  const values = await cpForm({
    title: `باطل کردن فاکتور ${n}؟`,
    body: document.getElementById("invoiceDetail").textContent || "این فاکتور باطل می‌شود.",
    tone: "danger",
    confirmLabel: "باطل کن",
    focusCancelFirst: true,
    fields: [{ name: "reason", kind: "textarea", label: "دلیل باطل" }],
    validate: (v) => (!v.reason ? { reason: "دلیل باطل الزامی است." } : null),
    onSubmit: async (v) => {
      await api(`/api/operator/invoices/${n}/void`, { method: "POST", body: JSON.stringify({ reason: v.reason }) });
      document.getElementById("invoiceDetail").textContent = "باطل شد.";
      cpToast({ tone: "success", text: "فاکتور باطل شد." });
    },
  });
  if (values == null) return;
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
  const tile = (lastGridTiles || []).find((x) => x.sessionId === selectedSessionId);
  const minutes = await cpPrompt({
    title: "رزرو زمان",
    body: tile?.id,
    confirmLabel: "ثبت رزرو",
    field: {
      kind: "number",
      label: "دقیقه",
      placeholder: "۶۰",
      positive: true,
      emptyLabel: "دقیقه را وارد کنید.",
      positiveErr: "دقیقه باید بیشتر از صفر باشد.",
      formatLabel: "عدد درست نیست؛ فقط رقم بنویسید.",
    },
    onSubmit: async (v) => {
      await api(`/api/operator/sessions/${selectedSessionId}/prepaid/duration`, {
        method: "POST",
        body: JSON.stringify({ minutes: v }),
      });
      await refreshPrepaidStatus();
    },
  });
  if (minutes == null) return;
});

bindClick("prepaidAmountBtn", async () => {
  if (!selectedSessionId) return;
  const tile = (lastGridTiles || []).find((x) => x.sessionId === selectedSessionId);
  const amount = await cpPrompt({
    title: "رزرو مبلغ",
    body: tile?.id,
    confirmLabel: "ثبت رزرو",
    field: { kind: "money", label: "مبلغ (تومان)", placeholder: "۵۵٬۰۰۰", positive: true },
    onSubmit: async (v) => {
      await api(`/api/operator/sessions/${selectedSessionId}/prepaid/amount`, {
        method: "POST",
        body: JSON.stringify({ amountToman: v }),
      });
      await refreshPrepaidStatus();
    },
  });
  if (amount == null) return;
});

bindClick("mgrGiftBtn", async () => {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) return showError("عضو پیوسته نیست.");
  const gift = await cpPrompt({
    title: "اعتبار هدیه",
    body: linked.name,
    confirmLabel: "ثبت هدیه",
    field: { kind: "money", label: "هدیه (تومان)", positive: true },
    validate: null,
    onSubmit: async (v) => {
      if (v <= 0) throw new Error("مبلغ باید عدد صحیح مثبت باشد.");
      await api(`/api/operator/members/customers/${linked.id}/wallet/gift`, {
        method: "POST",
        body: JSON.stringify({ giftToman: v }),
      });
      await refreshWalletBalance();
      cpToast({ tone: "success", text: "هدیه ثبت شد." });
    },
  });
  if (gift == null) return;
});

bindClick("mgrPairingCodeBtn", async () => {
  const stationId = document.getElementById("mgrPairStationId").value.trim();
  if (!stationId) return showError("شناسه ایستگاه را وارد کنید.");
  const res = await api(`/api/operator/stations/${stationId}/pairing-code`, { method: "POST" });
  await cpAlert({
    title: "کد جفت‌سازی",
    body: `این کد را روی ایستگاه ${stationId} وارد کنید.`,
    codeDisplay: String(res.code),
  });
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
  const values = await cpForm({
    title: "مانده افتتاحیه",
    body: `${linked.name}. فقط یک بار برای هر عضو ثبت می‌شود.`,
    confirmLabel: "ثبت مانده",
    fields: [
      { name: "realToman", kind: "money", label: "پول واقعی (تومان)", required: false, allowZero: true, positive: false },
      { name: "giftToman", kind: "money", label: "هدیه (تومان)", required: false, allowZero: true, positive: false },
      {
        name: "debtToman",
        kind: "money",
        label: "بدهی (تومان)",
        required: false,
        allowZero: true,
        positive: true,
        help: "بدهی را به صورت عدد مثبت وارد کنید.",
      },
      { name: "lifetimeHours", kind: "number", label: "ساعت بازی", required: false },
    ],
    validate: (v) => {
      const has =
        v.realToman != null || v.giftToman != null || v.debtToman != null || v.lifetimeHours != null;
      if (!has) return { _form: "دست‌کم یکی از مبلغ‌ها یا ساعت بازی را وارد کنید." };
      if ((v.realToman || 0) > 0 && (v.debtToman || 0) > 0) return { debtToman: "پول واقعی و بدهی را با هم وارد نکنید." };
      return null;
    },
    onSubmit: async (v) => {
      const body = {};
      if (v.realToman != null) body.realToman = v.realToman;
      if (v.giftToman != null) body.giftToman = v.giftToman;
      if (v.debtToman != null) body.debtToman = v.debtToman;
      if (v.lifetimeHours != null) body.lifetimeHours = v.lifetimeHours;
      await api(`/api/operator/members/customers/${linked.id}/opening-balance`, {
        method: "POST",
        body: JSON.stringify(body),
      });
      await refreshWalletBalance();
    },
  });
  if (values == null) return;
});

bindClick("mgrFloorBtn", async () => {
  const values = await cpForm({
    title: "سقف نسیه",
    body: "خالی یا ۰ یعنی نسیه ممنوع. فقط برای کنسول و میز.",
    confirmLabel: "ذخیره",
    fields: [
      {
        name: "mode",
        kind: "radio",
        label: "سقف نسیه",
        value: "none",
        options: [
          { value: "none", label: "بدون نسیه" },
          { value: "limited", label: "حداکثر بدهی مجاز" },
        ],
      },
      {
        name: "maxDebt",
        kind: "money",
        label: "حداکثر بدهی مجاز (تومان)",
        required: false,
        positive: true,
        help: "مقدار فعلی نامعلوم",
      },
    ],
    validate: (v) => {
      if (v.mode === "limited" && (!v.maxDebt || v.maxDebt <= 0)) return { maxDebt: "حداکثر بدهی را وارد کنید." };
      return null;
    },
    onSubmit: async (v) => {
      const floorToman = v.mode === "none" ? null : -Math.abs(v.maxDebt);
      await api("/api/operator/members/settings/debt-floor", {
        method: "PUT",
        body: JSON.stringify({ floorToman }),
      });
      cpToast({ tone: "success", text: "سقف نسیه ذخیره شد." });
    },
  });
  if (values == null) return;
});

bindClick("mgrBirthdayBtn", async () => {
  const cur = (await api("/api/operator/members/settings/birthday")) || {};
  const values = await cpForm({
    title: CP_TEXT.f12.title,
    confirmLabel: "ذخیره",
    fields: [
      { name: "percent", kind: "number", label: CP_TEXT.f12.percent, value: cur.percent ?? "" },
      {
        name: "windowKind",
        kind: "radio",
        label: CP_TEXT.f12.windowKind,
        value: cur.windowKind || "birth_month",
        options: [
          { value: "birth_month", label: CP_TEXT.f12.kind.birth_month },
          { value: "day_window", label: CP_TEXT.f12.kind.day_window },
          { value: "exact_day", label: CP_TEXT.f12.kind.exact_day },
        ],
      },
      { name: "windowDays", kind: "number", label: CP_TEXT.f12.windowDays, value: cur.windowDays ?? 1, required: false },
      {
        name: "anchor",
        kind: "radio",
        label: CP_TEXT.f12.anchor,
        value: cur.anchor || "on_and_after",
        options: [
          { value: "on_and_after", label: CP_TEXT.f12.anchorOpt.on_and_after },
          { value: "around", label: CP_TEXT.f12.anchorOpt.around },
        ],
      },
    ],
    validate: (v) => {
      const errs = {};
      const pct = cpParseInteger(String(v.percent));
      if (pct == null || pct < 0) errs.percent = "درصد را وارد کنید.";
      if (v.windowKind === "day_window") {
        const days = cpParseInteger(String(v.windowDays));
        if (days == null || days < 1) errs.windowDays = CP_TEXT.f12.errDays;
      }
      return Object.keys(errs).length ? errs : null;
    },
    onSubmit: async (v) => {
      await api("/api/operator/members/settings/birthday", {
        method: "PUT",
        body: JSON.stringify({
          percent: cpParseInteger(String(v.percent)),
          windowKind: v.windowKind,
          windowDays: v.windowKind === "day_window" ? cpParseInteger(String(v.windowDays)) : cur.windowDays ?? 1,
          anchor: v.windowKind === "day_window" ? v.anchor : cur.anchor || "on_and_after",
        }),
      });
      cpToast({ tone: "success", text: CP_TEXT.f12.done });
    },
  });
  if (values == null) return;
});

bindClick("mgrCustBirthdayBtn", async () => {
  const linked = await resolveLinkedCustomerForWallet();
  if (!linked) return showError("عضو پیوسته نیست.");
  const values = await cpForm({
    title: "تاریخ تولد عضو",
    body: linked.name,
    confirmLabel: "ذخیره",
    fields: [
      { name: "year", kind: "number", label: "سال", value: linked.birthYear || 1380 },
      {
        name: "month",
        kind: "select",
        label: "ماه",
        value: String(linked.birthMonth || 1),
        options: CP_JALALI_MONTHS.map((label, i) => ({ value: String(i + 1), label })),
      },
      { name: "day", kind: "number", label: "روز", value: linked.birthDay || 1 },
    ],
    validate: (v) => {
      const msg = validateJalaliBirth(Number(v.year), Number(v.month), Number(v.day));
      return msg ? { _form: msg } : null;
    },
    onSubmit: async (v) => {
      await api(`/api/operator/members/customers/${linked.id}/birthday`, {
        method: "PUT",
        body: JSON.stringify({ year: Number(v.year), month: Number(v.month), day: Number(v.day) }),
      });
      cpToast({ tone: "success", text: "تاریخ تولد ذخیره شد." });
    },
  });
  if (values == null) return;
});

bindClick("mgrLadderBtn", async () => {
  const values = await cpForm({
    title: CP_TEXT.f14.title,
    confirmLabel: CP_TEXT.f14.save,
    wide: true,
    fields: [
      {
        name: "ladder",
        kind: "custom",
        label: "",
        render: (host, session) => beginLevelLadderForm(host, session),
        read: (session) => readLevelLadderEditor(session),
      },
    ],
    onSubmit: async (v) => {
      try {
        await api("/api/operator/members/settings/level-ladder", { method: "PUT", body: JSON.stringify(v.ladder) });
        cpToast({ tone: "success", text: CP_TEXT.f14.done });
      } catch (e) {
        const msg =
          e && e.code === "invalid_level"
            ? CP_TEXT.f14.err.server
            : e && e.code === "forbidden"
              ? CP_TEXT.f14.err.forbidden
              : CP_TEXT.f14.err.saveFail;
        throw Object.assign(new Error(msg), { code: e?.code, message: msg });
      }
    },
  });
  if (values == null) return;
});

bindClick("mgrGiftCapBtn", async () => {
  const cur = await api("/api/operator/members/settings/operator-gift-cap");
  const values = await cpForm({
    title: "سقف هدیه اپراتور",
    confirmLabel: "ذخیره",
    fields: [
      { name: "maxToman", kind: "money", label: "سقف مبلغ هدیه (تومان)", value: cur.maxToman ?? 0, allowZero: true, positive: false },
      { name: "maxPercent", kind: "number", label: "سقف درصد هدیه", value: cur.maxPercent ?? 0 },
    ],
    validate: (v) => {
      const p = cpParseInteger(String(v.maxPercent));
      if (p == null || p < 0 || p > 100) return { maxPercent: "درصد باید بین ۰ و ۱۰۰ باشد." };
      return null;
    },
    onSubmit: async (v) => {
      await api("/api/operator/members/settings/operator-gift-cap", {
        method: "PUT",
        body: JSON.stringify({ maxToman: v.maxToman ?? 0, maxPercent: cpParseInteger(String(v.maxPercent)) }),
      });
      cpToast({ tone: "success", text: "سقف هدیه ذخیره شد." });
    },
  });
  if (values == null) return;
});

bindClick("mgrCorrectionsBtn", async () => {
  const rows = (await api("/api/operator/members/wallet/corrections")) || [];
  await cpAlert({
    title: "اصلاح‌های شارژ",
    wide: true,
    rows: rows.map((r) => ({
      title: fmtTehranDateTime(r.atUtc),
      subtitle: `${paymentMethodFa(r.method || "")} · مبلغ ${r.beforeAmountToman} ← ${r.afterAmountToman} · هدیه ${r.beforeGiftToman} ← ${r.afterGiftToman}${r.description ? ` · ${r.description}` : ""}`,
    })),
    emptyText: rows.length ? undefined : "اصلاحی ثبت نشده است.",
  });
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
  const v = await cpPrompt({
    title: "باز کردن شیفت",
    confirmLabel: "باز کردن شیفت",
    field: {
      kind: "money",
      label: "تنخواه (تومان)",
      required: false,
      allowZero: true,
      positive: false,
      help: "خالی یعنی ۰ تومان.",
    },
    onSubmit: async (n) => {
      const openingFloatToman = n == null ? 0 : n;
      await api("/api/operator/shift/open", { method: "POST", body: JSON.stringify({ openingFloatToman }) });
      await refreshShiftState();
    },
  });
  if (v == null) return;
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

function escapeHtmlText(value) {
  return String(value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function renderReportsTable(title, headers, rows) {
  const head = headers.map((h) => `<th>${escapeHtmlText(h)}</th>`).join("");
  const body = rows
    .map((r) => `<tr>${r.map((c) => `<td>${escapeHtmlText(c)}</td>`).join("")}</tr>`)
    .join("");
  return `<h3>${escapeHtmlText(title)}</h3><table dir="rtl" border="1" cellpadding="4"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
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
  host.replaceChildren();
  if (!openSessions?.length) {
    const p = document.createElement("p");
    p.textContent = "جلسهٔ بازی باز نیست.";
    host.appendChild(p);
    return;
  }
  for (const s of openSessions) {
    const row = document.createElement("div");
    row.dataset.session = String(s.sessionId);
    const strong = document.createElement("strong");
    strong.textContent = s.stationId ?? "";
    row.appendChild(strong);
    row.appendChild(document.createTextNode(` — ${s.status ?? ""} `));
    const settle = document.createElement("button");
    settle.type = "button";
    settle.dataset.action = "settle";
    settle.dataset.session = String(s.sessionId);
    settle.textContent = "تسویه در همین بستن";
    const carry = document.createElement("button");
    carry.type = "button";
    carry.dataset.action = "carry";
    carry.dataset.session = String(s.sessionId);
    carry.textContent = "انتقال به دوره بعد";
    row.append(settle, carry);
    host.appendChild(row);
  }
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
  const cashRaw = document.getElementById("closeCash").value;
  const readerRaw = document.getElementById("closeReader").value;
  const parseCloseToman = (raw) => {
    if (typeof window.cpParseNumber !== "function") return null;
    const trimmed = String(raw ?? "").trim();
    if (!trimmed) return 0;
    return window.cpParseNumber(trimmed, { allowZero: true, positive: false });
  };
  const actualCashToman = parseCloseToman(cashRaw);
  const actualCardReaderToman = parseCloseToman(readerRaw);
  if (actualCashToman == null || actualCardReaderToman == null) {
    showError("مبلغ درست نیست؛ فقط رقم بنویسید.");
    return;
  }
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
  const games = await ss14FetchGamesForOption(mode, playerCount);
  const picked = await cpPick({
    title: "انتخاب بازی",
    body: hint || "",
    items: [
      { id: "", title: "بدون بازی خاص", subtitle: "" },
      ...games.map((g) => ({ id: String(g.id), title: g.name, subtitle: "", gameTitleId: g.id })),
    ],
    emptyText: "بازی‌ای موجود نیست.",
  });
  if (picked == null) return null;
  const gameTitleId = picked.id === "" ? null : Number(picked.id);
  try {
    if (onConfirm) await onConfirm(gameTitleId);
  } catch (_) {
    /* api() already surfaced the error */
  }
  return gameTitleId;
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
  if (typeof window.cpParseNumber === "function") {
    return window.cpParseNumber(raw, { positive: true, allowSeparators: true });
  }
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
