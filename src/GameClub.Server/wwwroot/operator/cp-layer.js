/* Checkpoint operator cp* in-page forms layer — MVP phase 1 */
(function () {
  const BP = 768;
  const TOAST_MS = 4000;
  const TIMEOUT_MSG = "زمان درخواست تمام شد؛ دوباره تلاش کنید.";

  const cpLayer = {
    active: null,
    queue: [],
    _toastTimer: null,
    _historyPushed: false,
    _closingViaHistory: false,
  };

  function isPhone() {
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") return true;
    return window.matchMedia(`(max-width: ${BP - 0.02}px)`).matches;
  }

  function ensureRoot() {
    let root = document.getElementById("cpLayer");
    if (!root) {
      root = document.createElement("div");
      root.id = "cpLayer";
      root.setAttribute("aria-live", "polite");
      document.body.appendChild(root);
    }
    return root;
  }

  function lockBodyScroll(lock) {
    if (lock) {
      document.body.dataset.cpScrollY = String(window.scrollY);
      document.body.style.overflow = "hidden";
    } else {
      document.body.style.overflow = "";
      const y = Number(document.body.dataset.cpScrollY || "0");
      delete document.body.dataset.cpScrollY;
      try {
        if (typeof window.scrollTo === "function") window.scrollTo(0, y);
      } catch (_) {}
    }
  }

  function pushHistory(id) {
    try {
      const url = typeof window !== "undefined" && window.location ? window.location.href : "";
      history.pushState({ cpDialog: id }, "", url);
      cpLayer._historyPushed = true;
    } catch (_) {}
  }

  function popHistoryIfNeeded() {
    if (!cpLayer._historyPushed) return;
    if (cpLayer._closingViaHistory) {
      cpLayer._historyPushed = false;
      return;
    }
    cpLayer._closingViaHistory = true;
    cpLayer._historyPushed = false;
    try {
      history.back();
    } catch (_) {}
    window.setTimeout(() => {
      cpLayer._closingViaHistory = false;
    }, 0);
  }

  if (typeof window !== "undefined" && typeof window.addEventListener === "function") {
  window.addEventListener("popstate", (ev) => {
    if (cpLayer._closingViaHistory) return;
    const active = cpLayer.active;
    if (!active) return;
  if (active._onBackStep) {
      const handled = active._onBackStep();
      if (handled) {
        pushHistory(active.id);
        return;
      }
    }
    cpLayer._closingViaHistory = true;
    if (!active.busy) active.cancel("history");
  });
  }

  function cpIsDigitChar(ch) {
    return (ch >= "0" && ch <= "9") || (ch >= "\u06F0" && ch <= "\u06F9") || (ch >= "\u0660" && ch <= "\u0669");
  }

  function cpIsMoneySepChar(ch) {
    return ch === "," || ch === "\u060C" || ch === "\u066C";
  }

  /** Reject decimals, signs, inner space, or any character outside digits / (optional) money separators. */
  function cpHasIllegalNumericChars(trimmed, allowSeparators) {
    for (const ch of trimmed) {
      if (cpIsDigitChar(ch)) continue;
      if (allowSeparators && cpIsMoneySepChar(ch)) continue;
      return true;
    }
    return false;
  }

  function cpHasValidThousandsGrouping(trimmed) {
    const groups = [];
    let cur = "";
    for (const ch of trimmed) {
      if (cpIsMoneySepChar(ch)) {
        if (cur.length === 0) return false;
        groups.push(cur);
        cur = "";
      } else if (cpIsDigitChar(ch)) {
        cur += ch;
      } else {
        return false;
      }
    }
    if (cur.length === 0 && groups.length > 0) return false;
    if (cur.length > 0) groups.push(cur);
    if (groups.length === 0) return false;
    if (groups.length === 1) return true;
    if (groups[0].length < 1 || groups[0].length > 3) return false;
    for (let i = 1; i < groups.length; i++) {
      if (groups[i].length !== 3) return false;
    }
    return true;
  }

  function digitsOnlyFromMoneyRaw(raw) {
    if (raw == null) return "";
    const fa = "۰۱۲۳۴۵۶۷۸۹";
    let out = "";
    for (const ch of String(raw)) {
      if (ch >= "0" && ch <= "9") out += ch;
      else if (ch >= "\u06F0" && ch <= "\u06F9") out += String(fa.indexOf(ch));
      else if (ch >= "\u0660" && ch <= "\u0669") out += String("0123456789"[ch.charCodeAt(0) - 0x0660]);
    }
    return out;
  }

  const CP_ERR = {
    moneyRequired: "مبلغ را وارد کنید.",
    moneyFormat: "مبلغ درست نیست؛ فقط رقم بنویسید.",
    moneyLeadingZero: "مبلغ نباید با صفر شروع شود.",
    moneyTooLong: "مبلغ نباید بیشتر از ۱۵ رقم باشد.",
    moneyPositive: "مبلغ باید عدد صحیح مثبت باشد.",
    moneyNonNegative: "مبلغ نمی‌تواند منفی باشد.",
    f12PercentEmpty: "درصد تولد را وارد کنید.",
    numberFormat: "عدد درست نیست؛ فقط رقم بنویسید.",
    numberTooLong: "عدد نباید بیشتر از ۱۵ رقم باشد.",
    generic: "کار انجام نشد. دوباره تلاش کنید.",
    f07Empty: "دقیقه را وارد کنید.",
    f07Zero: "دقیقه باید بیشتر از صفر باشد.",
  };

  function cpParseNumberResult(raw, opts = {}) {
    const allowZero = opts.allowZero === true;
    const positive = opts.positive !== false;
    const allowSeparators = opts.allowSeparators !== false;
    const trimmed = raw == null ? "" : String(raw).trim();
    if (!trimmed) return { ok: false, reason: "empty" };
    if (cpHasIllegalNumericChars(trimmed, allowSeparators)) return { ok: false, reason: "format" };
    if (allowSeparators && !cpHasValidThousandsGrouping(trimmed)) return { ok: false, reason: "format" };
    const s = digitsOnlyFromMoneyRaw(trimmed);
    if (!s) return { ok: false, reason: "format" };
    if (s.length > 15) return { ok: false, reason: "too_long" };
    if (s.length > 1 && s[0] === "0") return { ok: false, reason: "leading_zero" };
    const n = Number(s);
    if (!Number.isFinite(n) || !Number.isInteger(n)) return { ok: false, reason: "format" };
    if (n < 0) return { ok: false, reason: "format" };
    if (positive && n <= 0 && !(allowZero && n === 0)) return { ok: false, reason: "non_positive" };
    if (n === 0 && !allowZero) return { ok: false, reason: "zero" };
    return { ok: true, value: n };
  }

  function cpParseNumber(raw, opts = {}) {
    const r = cpParseNumberResult(raw, opts);
    return r.ok ? r.value : null;
  }

  function cpMoneyMessageFromResult(r, field) {
    if (r.ok) return null;
    switch (r.reason) {
      case "empty":
        return field.emptyLabel || CP_ERR.moneyRequired;
      case "format":
        return field.invalidFormatLabel || CP_ERR.moneyFormat;
      case "leading_zero":
        return CP_ERR.moneyLeadingZero;
      case "too_long":
        return CP_ERR.moneyTooLong;
      case "zero":
      case "non_positive":
        return CP_ERR.moneyPositive;
      default:
        return CP_ERR.moneyFormat;
    }
  }

  function cpParseIntegerResult(raw) {
    const trimmed = raw == null ? "" : String(raw).trim();
    if (!trimmed) return { ok: false, reason: "empty" };
    if (cpHasIllegalNumericChars(trimmed, false)) return { ok: false, reason: "format" };
    const s = digitsOnlyFromMoneyRaw(trimmed);
    if (!s) return { ok: false, reason: "format" };
    if (s.length > 15) return { ok: false, reason: "too_long" };
    const n = Number(s);
    if (!Number.isInteger(n)) return { ok: false, reason: "format" };
    return { ok: true, value: n };
  }

  function cpParseInteger(raw) {
    const r = cpParseIntegerResult(raw);
    return r.ok ? r.value : null;
  }

  const SESSION_STALE_MSG = "جلسه تغییر کرده است. صفحه تازه شد؛ دوباره تلاش کنید.";

  const ERROR_MAP = {
    payment_invalid: "دست‌کم یک خط پرداخت با مبلغ بیشتر از صفر لازم است.",
    wallet_requires_member: "برای پرداخت کیف پول، عضو را پیوند دهید.",
    reason_required: "دلیل باطل را وارد کنید.",
    forbidden: "فقط مدیر می‌تواند فاکتور را باطل کند.",
    forbidden_manager: "فقط مدیر یا مالک می‌تواند این کار را انجام دهد.",
    forbidden_ladder: "فقط مدیر یا مالک می‌تواند جدول سطح‌ها را عوض کند.",
    invalid_gift: "هدیه نمی‌تواند منفی باشد.",
    invalid_amount: "مبلغ نامعتبر است.",
    invalid_amount_gift: "هدیه نمی‌تواند منفی باشد.",
    opening_exists: "مانده افتتاحیهٔ این عضو قبلاً ثبت شده است.",
    invalid_floor: "سقف نسیه باید صفر یا منفی باشد.",
    same_station: "این ایستگاه الان نمی‌تواند جلسه بگیرد. فهرست تازه شد.",
    transfer_type: "این ایستگاه الان نمی‌تواند جلسه بگیرد. فهرست تازه شد.",
    inactive: "این ایستگاه الان نمی‌تواند جلسه بگیرد. فهرست تازه شد.",
    target_unavailable: "این ایستگاه الان نمی‌تواند جلسه بگیرد. فهرست تازه شد.",
    no_group: "این ایستگاه الان نمی‌تواند جلسه بگیرد. فهرست تازه شد.",
    station_busy: "این ایستگاه الان نمی‌تواند جلسه بگیرد. فهرست تازه شد.",
    stale_version: SESSION_STALE_MSG,
    session_busy: SESSION_STALE_MSG,
    not_transferable: SESSION_STALE_MSG,
    awaiting_payment: SESSION_STALE_MSG,
    closed: SESSION_STALE_MSG,
    invalid_session: SESSION_STALE_MSG,
    stale_bill: SESSION_STALE_MSG,
    invalid_level: "جدول سطح‌ها درست نیست. نام‌ها، ساعت‌ها و درصدها را بررسی کنید.",
    invalid_date: "تاریخ درست نیست.",
    invalid_policy: "تنظیم تولد نامعتبر است.",
    invalid_window_days: "دست‌کم ۱ روز.",
    description_required: "توضیح الزامی است.",
    no_change: "تغییری نسبت به مقدار فعلی ندارد.",
    gift_cap_exceeded: "هدیه از سقف اپراتور بیشتر است.",
    gift_not_allowed_with_debt:
      "مشتری بدهی دارد و این شارژ بدهی را کامل صاف نمی‌کند؛ بدون هدیه دوباره ثبت کنید.",
    invalid_money: "مبلغ درست نیست.",
    invalid_opening: "مانده افتتاحیه درست نیست.",
    invalid_method: "روش شارژ درست نیست.",
    no_open_shift: "شیفتی باز نیست. اول شیفت را باز کنید.",
    shift_closed: "شیفت بسته شده است.",
    not_found: "این مورد پیدا نشد. صفحه را تازه کنید.",
    not_pending: "این مورد قبلاً انجام شده است.",
    gift_already_spent: "هدیهٔ این شارژ خرج شده است.",
    correction_window_closed: "شیفت این شارژ بسته شده؛ فقط مدیر می‌تواند اصلاحش کند.",
    correction_exceeds_balance:
      "این اصلاح موجودی پرداختی مشتری را منفی می‌کند (مشتری قبلاً خرج کرده). اصلاح انجام نشد؛ به مدیر خبر دهید.",
    prepaid_exists: "این جلسه از قبل رزرو دارد.",
    below_minimum: "این مبلغ از حداقل زمان بازی تعرفه کمتر است.",
    not_console: "رزرو فقط برای کنسول و میز است.",
    invalid_state: "این جلسه فعال نیست.",
    invalid_minutes: CP_ERR.f07Zero,
    invalid_time: "زمان درست نیست.",
    billing_error: CP_ERR.generic,
    invalid_cap: "سقف هدیه درست نیست. مبلغ و درصد را بررسی کنید.",
    invalid_float: "تنخواه نمی‌تواند منفی باشد.",
    shift_open: "شیفت از قبل باز است.",
    shifts_disabled: "شیفت در تنظیمات خاموش است.",
    unauthorized: "دوباره وارد شوید.",
  };

  function cpTranslateApiError(code, message, ctx) {
    if (code === "forbidden") {
      if (ctx && ctx.scope === "level_ladder") return ERROR_MAP.forbidden_ladder;
      if (ctx && ctx.scope === "void_invoice") return ERROR_MAP.forbidden;
      if (ctx && ctx.scope === "manager_action") return ERROR_MAP.forbidden_manager;
    }
    if (code === "invalid_amount" && ctx && ctx.scope === "member_gift") {
      return ERROR_MAP.invalid_amount_gift;
    }
    if (code === "gift_not_allowed_with_debt" && message && /[\u0600-\u06FF]/.test(message)) {
      return message;
    }
    if (code && ERROR_MAP[code]) return ERROR_MAP[code];
    if (message && /[\u0600-\u06FF]/.test(message)) return message;
    return CP_ERR.generic;
  }

  function cpToast({ tone = "success", text }) {
    let host = document.getElementById("cpToastHost");
    if (!host) {
      host = document.createElement("div");
      host.id = "cpToastHost";
      document.body.appendChild(host);
    }
    host.replaceChildren();
    const el = document.createElement("div");
    el.className = `cp-toast cp-toast-${tone}`;
    el.setAttribute("role", "status");
    el.textContent = text;
    host.appendChild(el);
    clearTimeout(cpLayer._toastTimer);
    cpLayer._toastTimer = setTimeout(() => {
      if (host.contains(el)) host.replaceChildren();
    }, TOAST_MS);
    el.onclick = () => host.replaceChildren();
  }

  function fmtTomanDisplay(n) {
    if (n == null || n === "") return "";
    const num = Number(n);
    if (!Number.isFinite(num)) return "";
    return num.toLocaleString("fa-IR");
  }

  class LayerSession {
    constructor(spec, resolve) {
      this.id = newCommandIdForLayer();
      this.spec = spec;
      this.resolve = resolve;
      this.busy = false;
      this.fieldEls = {};
      this.serverErrorEl = null;
      this._rejectCancel = null;
    }

    setServerError(msg) {
      if (!this.serverErrorEl) return;
      this.serverErrorEl.textContent = msg || "";
      this.serverErrorEl.classList.toggle("hidden", !msg);
      if (msg) this.serverErrorEl.focus({ preventScroll: true });
    }

    cancel(reason) {
      if (this.busy) return;
      if (reason === "history") cpLayer._closingViaHistory = true;
      this._finish(null);
    }

    _detachKeydown() {
      const root = document.getElementById("cpLayer");
      if (root && this._keydownHandler) {
        root.removeEventListener("keydown", this._keydownHandler);
        this._keydownHandler = null;
      }
    }

    _finish(value) {
      this._detachKeydown();
      popHistoryIfNeeded();
      lockBodyScroll(false);
      const root = ensureRoot();
      root.replaceChildren();
      root.classList.add("hidden");
      const prev = cpLayer.active;
      cpLayer.active = null;
      if (prev === this) {
        this.resolve(value);
        drainQueue();
      }
    }

    async submit(values) {
      if (this.busy || this._submitInFlight) return;
      this._submitInFlight = true;
      try {
        const spec = this.spec;
        if (spec.validate) {
          const errs = spec.validate(values);
          if (errs) {
            this._applyFieldErrors(errs);
            return;
          }
        }
        if (typeof spec.onSubmit === "function") {
          this.busy = true;
          this._setBusyUi(true);
          try {
            await spec.onSubmit(values);
            this._finish(values);
          } catch (e) {
            if (typeof spec.mapApiError === "function") {
              const fieldErrs = spec.mapApiError(e, values);
              if (fieldErrs && typeof fieldErrs === "object") {
                this._applyFieldErrors(fieldErrs);
                return;
              }
            }
            const msg = e && e.message ? cpTranslateApiError(e.code, e.message) : TIMEOUT_MSG;
            this.setServerError(msg);
          } finally {
            this.busy = false;
            this._setBusyUi(false);
          }
        } else {
          this._finish(values);
        }
      } finally {
        this._submitInFlight = false;
      }
    }

    _setBusyUi(on) {
      const root = ensureRoot();
      root.querySelectorAll("input, select, textarea, button").forEach((el) => {
        if (el.dataset.cpRole === "submit" || el.dataset.cpRole === "cancel" || el.dataset.cpRole === "close") {
          el.disabled = on;
        } else if (on) el.disabled = true;
        else el.disabled = false;
      });
      const submit = root.querySelector('[data-cp-role="submit"]');
      if (submit) {
        submit.textContent = on ? "در حال ثبت…" : this.spec.confirmLabel || "ثبت";
      }
    }

    _applyFieldErrors(errs) {
      let firstFocus = null;
      for (const [name, msg] of Object.entries(errs)) {
        if (name === "_form") {
          this.setServerError(msg);
          continue;
        }
        const errEl = this.fieldEls[`${name}:err`] || document.querySelector(`[data-cp-ladder-err="${name}"]`);
        const input =
          this.fieldEls[name] || document.querySelector(`[data-cp-ladder-field="${name}"]`);
        if (errEl) {
          errEl.textContent = msg;
          errEl.classList.remove("hidden");
        }
        if (input) {
          input.setAttribute("aria-invalid", "true");
          if (!firstFocus) firstFocus = input;
        }
      }
      (firstFocus || this.serverErrorEl)?.focus({ preventScroll: true });
    }
  }

  function newCommandIdForLayer() {
    if (typeof newCommandId === "function") return newCommandId();
    return String(Date.now()) + Math.random();
  }

  function drainQueue() {
    if (cpLayer.active || !cpLayer.queue.length) return;
    const next = cpLayer.queue.shift();
    next();
  }

  function enqueueOpen(fn) {
    if (cpLayer.active) {
      return new Promise((resolve) => {
        cpLayer.queue.push(() => fn().then(resolve));
      });
    }
    return fn();
  }

  function renderShell(session) {
    const root = ensureRoot();
    root.classList.remove("hidden");
    root.classList.toggle("cp-phone", isPhone());
    root.classList.toggle("cp-desktop", !isPhone());
    const scrim = document.createElement("div");
    scrim.className = "cp-scrim";
    scrim.dataset.cpRole = "scrim";
    scrim.onclick = () => session.cancel("scrim");

    const sheet = document.createElement("div");
    sheet.className = "cp-sheet";
    sheet.setAttribute("role", session.spec.role || "dialog");
    sheet.setAttribute("aria-modal", "true");
    if (session.spec.wide) sheet.classList.add("cp-wide");

    const titleId = "cpTitle";
    const head = document.createElement("div");
    head.className = "cp-head";
    if (session.spec.onBack) {
      const back = document.createElement("button");
      back.type = "button";
      back.className = "cp-back";
      back.setAttribute("aria-label", "برگشت");
      back.textContent = "→";
      back.onclick = () => session.spec.onBack();
      head.appendChild(back);
    }
    const h = document.createElement("h2");
    h.id = titleId;
    h.className = "cp-title";
    h.textContent = session.spec.title || "";
    sheet.setAttribute("aria-labelledby", titleId);
    const close = document.createElement("button");
    close.type = "button";
    close.className = "cp-close";
    close.setAttribute("aria-label", "بستن");
    close.textContent = "×";
    close.dataset.cpRole = "close";
    close.onclick = () => session.cancel("close");
    head.append(h, close);
    sheet.appendChild(head);

    if (session.spec.body) {
      const p = document.createElement("p");
      p.className = "cp-body";
      p.textContent = session.spec.body;
      sheet.appendChild(p);
    }
    if (session.spec.warning) {
      const w = document.createElement("p");
      w.className = "cp-warning";
      w.textContent = session.spec.warning;
      sheet.appendChild(w);
    }

    const fieldsHost = document.createElement("div");
    fieldsHost.className = "cp-fields";
    sheet.appendChild(fieldsHost);

    session.serverErrorEl = document.createElement("p");
    session.serverErrorEl.className = "cp-server-error hidden";
    session.serverErrorEl.setAttribute("role", "alert");
    session.serverErrorEl.tabIndex = -1;
    sheet.appendChild(session.serverErrorEl);

    const foot = document.createElement("div");
    foot.className = "cp-foot";
    const cancelBtn = document.createElement("button");
    cancelBtn.type = "button";
    cancelBtn.className = "cp-btn cp-secondary";
    cancelBtn.dataset.cpRole = "cancel";
    cancelBtn.textContent = session.spec.cancelLabel || "انصراف";
    cancelBtn.onclick = () => session.cancel("cancel");
    const submitBtn = document.createElement("button");
    submitBtn.type = "button";
    submitBtn.className = `cp-btn ${session.spec.tone === "danger" ? "cp-danger" : "cp-primary"}`;
    submitBtn.dataset.cpRole = "submit";
    submitBtn.textContent = session.spec.confirmLabel || "ثبت";
    submitBtn.onclick = () => {
      const run = session.spec.collectAndSubmit;
      if (typeof run === "function") void run();
    };
    foot.append(cancelBtn, submitBtn);
    if (session.spec.role === "alertdialog" && !(session.spec.fields?.length)) {
      submitBtn.hidden = true;
    }
    sheet.appendChild(foot);

    root.replaceChildren(scrim, sheet);
    lockBodyScroll(true);
    pushHistory(session.id);
    session._detachKeydown();
    const fieldCount = session.spec.fields?.length ?? 0;
    session._keydownHandler = (e) => {
      if (e.key === "Escape") {
        e.preventDefault();
        if (!session.busy) session.cancel("escape");
        return;
      }
      if ((e.ctrlKey || e.metaKey) && e.key === "Enter") {
        e.preventDefault();
        if (!session.busy && !session._submitInFlight && typeof session.spec.collectAndSubmit === "function") {
          void session.spec.collectAndSubmit();
        }
        return;
      }
      if (e.key === "Enter" && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
        if (e.target?.tagName === "TEXTAREA") return;
        if (fieldCount === 1 && !session.busy && !session._submitInFlight && typeof session.spec.collectAndSubmit === "function") {
          e.preventDefault();
          void session.spec.collectAndSubmit();
        }
      }
    };
    root.addEventListener("keydown", session._keydownHandler);
    sheet.tabIndex = -1;
    return {
      fieldsHost,
      submitBtn,
      cancelBtn,
      foot,
      sheet,
      focusTarget: session.spec.focusCancelFirst ? cancelBtn : submitBtn,
    };
  }

  function setCpFieldDisabled(session, name, disabled) {
    const wrap = session.fieldEls[`${name}:wrap`];
    const control = session.fieldEls[name];
    if (!wrap) return;
    wrap.setAttribute("aria-disabled", disabled ? "true" : "false");
    if (control) {
      if (control.role === "switch") {
        control.disabled = disabled;
        control.tabIndex = disabled ? -1 : 0;
      } else {
        control.disabled = disabled;
      }
    }
    if (wrap.tagName === "FIELDSET") {
      wrap.querySelectorAll("input").forEach((inp) => {
        inp.disabled = disabled;
      });
    }
  }

  function buildMoneyInput(field, session) {
    const wrap = document.createElement("label");
    wrap.className = "cp-field";
    wrap.dataset.cpFieldWrap = field.name || "value";
    const lbl = document.createElement("span");
    lbl.textContent = field.label + (field.required === false ? " (اختیاری)" : "");
    wrap.appendChild(lbl);
    const row = document.createElement("div");
    row.className = "cp-money-row";
    const input = document.createElement("input");
    input.type = "text";
    input.inputMode = "numeric";
    input.dir = "ltr";
    input.autocomplete = "off";
    input.dataset.cpField = field.name || "value";
    if (field.placeholder) input.placeholder = field.placeholder;
    if (field.value != null) input.value = fmtTomanDisplay(field.value);
    const suf = document.createElement("span");
    suf.className = "cp-suffix";
    suf.textContent = "تومان";
    row.append(input, suf);
    wrap.appendChild(row);
    const err = document.createElement("span");
    err.className = "cp-field-error hidden";
    wrap.appendChild(err);
    if (field.help) {
      const h = document.createElement("span");
      h.className = "cp-help";
      h.textContent = field.help;
      wrap.appendChild(h);
    }
    session.fieldEls[field.name || "value"] = input;
    session.fieldEls[`${field.name || "value"}:err`] = err;
    session.fieldEls[`${field.name || "value"}:wrap`] = wrap;
    return { wrap, input, err };
  }

  function readMoneyField(input, field) {
    const allowZero = field.allowZero === true;
    const positive = field.positive !== false;
    const parsed = cpParseNumberResult(input.value, { allowZero, positive });
    if (!parsed.ok) {
      if (parsed.reason === "empty" && field.required === false) return { empty: true };
      return { error: cpMoneyMessageFromResult(parsed, field) };
    }
    return { value: parsed.value };
  }

  function readNumberField(input, field) {
    const trimmed = String(input.value ?? "").trim();
    if (!trimmed) {
      if (field.required === false) return { skip: true };
      return { error: field.emptyLabel || CP_ERR.f07Empty };
    }
    const parsed = cpParseIntegerResult(input.value);
    if (!parsed.ok) {
      if (parsed.reason === "too_long") return { error: field.tooLongLabel || CP_ERR.numberTooLong };
      return { error: field.formatLabel || CP_ERR.numberFormat };
    }
    if (field.positive === true && parsed.value <= 0) {
      return { error: field.positiveErr || CP_ERR.f07Zero };
    }
    return { value: parsed.value };
  }

  function openForm(spec) {
    return enqueueOpen(
      () =>
        new Promise((resolve) => {
          const session = new LayerSession(spec, resolve);
          cpLayer.active = session;
          session._onBackStep = spec.onBackStep || null;

          const render = () => {
            const ui = renderShell(session);
            const host = ui.fieldsHost;
            host.replaceChildren();
            for (const field of spec.fields || []) {
              if (field.kind === "money") {
                host.appendChild(buildMoneyInput(field, session).wrap);
              } else if (field.kind === "number") {
                const w = document.createElement("label");
                w.className = "cp-field";
                const labelSpan = document.createElement("span");
                labelSpan.textContent = `${field.label}${field.required === false ? " (اختیاری)" : ""}`;
                w.appendChild(labelSpan);
                const input = document.createElement("input");
                input.type = "text";
                input.inputMode = "numeric";
                input.dir = "ltr";
                input.dataset.cpField = field.name;
                if (field.value != null) input.value = String(field.value);
                w.appendChild(input);
                const err = document.createElement("span");
                err.className = "cp-field-error hidden";
                w.appendChild(err);
                session.fieldEls[field.name] = input;
                session.fieldEls[`${field.name}:err`] = err;
                host.appendChild(w);
              } else if (field.kind === "textarea") {
                const w = document.createElement("label");
                w.className = "cp-field";
                const labelSpan = document.createElement("span");
                labelSpan.textContent = field.label || "";
                w.appendChild(labelSpan);
                const ta = document.createElement("textarea");
                ta.rows = 3;
                ta.dataset.cpField = field.name;
                if (field.value) ta.value = field.value;
                w.appendChild(ta);
                const err = document.createElement("span");
                err.className = "cp-field-error hidden";
                session.fieldEls[field.name] = ta;
                session.fieldEls[`${field.name}:err`] = err;
                w.appendChild(err);
                host.appendChild(w);
              } else if (field.kind === "select") {
                const w = document.createElement("label");
                w.className = "cp-field";
                const labelSpan = document.createElement("span");
                labelSpan.textContent = field.label || "";
                w.appendChild(labelSpan);
                const sel = document.createElement("select");
                sel.dataset.cpField = field.name;
                for (const opt of field.options || []) {
                  const o = document.createElement("option");
                  o.value = opt.value;
                  o.textContent = opt.label;
                  if (field.value === opt.value) o.selected = true;
                  sel.appendChild(o);
                }
                w.appendChild(sel);
                host.appendChild(w);
                session.fieldEls[field.name] = sel;
              } else if (field.kind === "switch") {
                const row = document.createElement("div");
                row.className = "cp-field cp-switch-row";
                row.dataset.cpFieldWrap = field.name;
                const head = document.createElement("div");
                head.className = "cp-switch-head";
                const lbl = document.createElement("span");
                lbl.className = "cp-switch-label";
                lbl.textContent = field.label || "";
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "cp-switch";
                btn.setAttribute("role", "switch");
                btn.setAttribute("aria-checked", field.value ? "true" : "false");
                btn.dataset.cpField = field.name;
                btn.dir = "ltr";
                const on = !!field.value;
                btn.setAttribute("aria-labelledby", `cp-sw-lbl-${field.name}`);
                lbl.id = `cp-sw-lbl-${field.name}`;
                const toggle = () => {
                  const next = btn.getAttribute("aria-checked") !== "true";
                  btn.setAttribute("aria-checked", next ? "true" : "false");
                  if (typeof field.onChange === "function") field.onChange(session, next);
                  else if (typeof spec.onFieldChange === "function") spec.onFieldChange(session, field.name, next);
                };
                btn.addEventListener("click", toggle);
                btn.addEventListener("keydown", (e) => {
                  if (e.key === " " || e.key === "Enter") {
                    e.preventDefault();
                    toggle();
                  }
                });
                head.append(lbl, btn);
                row.appendChild(head);
                if (field.help) {
                  const h = document.createElement("span");
                  h.className = "cp-help";
                  h.textContent = field.help;
                  row.appendChild(h);
                }
                host.appendChild(row);
                session.fieldEls[field.name] = btn;
                session.fieldEls[`${field.name}:wrap`] = row;
              } else if (field.kind === "radio") {
                const w = document.createElement("fieldset");
                w.className = "cp-field cp-radio";
                w.dataset.cpFieldWrap = field.name;
                const leg = document.createElement("legend");
                leg.textContent = field.label;
                w.appendChild(leg);
                for (const opt of field.options || []) {
                  const lab = document.createElement("label");
                  lab.className = "cp-radio-row";
                  const inp = document.createElement("input");
                  inp.type = "radio";
                  inp.name = field.name;
                  inp.value = opt.value;
                  if (field.value === opt.value) inp.checked = true;
                  lab.append(inp, document.createTextNode(opt.label));
                  w.appendChild(lab);
                }
                host.appendChild(w);
                session.fieldEls[field.name] = w;
                session.fieldEls[`${field.name}:wrap`] = w;
              } else if (field.kind === "checkbox") {
                const lab = document.createElement("label");
                lab.className = "cp-field cp-check";
                const inp = document.createElement("input");
                inp.type = "checkbox";
                inp.dataset.cpField = field.name;
                inp.checked = !!field.value;
                lab.append(inp, document.createTextNode(field.label));
                host.appendChild(lab);
                session.fieldEls[field.name] = inp;
              } else if (field.kind === "custom" && typeof field.render === "function") {
                field.render(host, session);
              } else {
                const w = document.createElement("label");
                w.className = "cp-field";
                const labelSpan = document.createElement("span");
                labelSpan.textContent = field.label || "";
                w.appendChild(labelSpan);
                const input = document.createElement("input");
                input.type = "text";
                input.dataset.cpField = field.name;
                input.dir = field.ltr ? "ltr" : "auto";
                if (field.value != null) input.value = field.value;
                w.appendChild(input);
                host.appendChild(w);
                session.fieldEls[field.name] = input;
              }
            }
            (host.querySelector("input, select, textarea") || ui.focusTarget)?.focus({ preventScroll: true });
          };

          session.spec.collectAndSubmit = async () => {
            if (session.busy || session._submitInFlight) return;
            const values = {};
            const errs = {};
            const initials = spec.initialValues || {};
            for (const field of spec.fields || []) {
              const wrap = session.fieldEls[`${field.name}:wrap`];
              const rowDisabled = wrap?.getAttribute("aria-disabled") === "true";
              if (rowDisabled && field.name in initials) {
                values[field.name] = initials[field.name];
                continue;
              }
              if (field.kind === "money") {
                const input = session.fieldEls[field.name];
                const r = readMoneyField(input, field);
                if (r.error) errs[field.name] = r.error;
                else if (!r.empty) values[field.name] = r.value;
              } else if (field.kind === "number") {
                const input = session.fieldEls[field.name];
                const nr = readNumberField(input, field);
                if (nr.error) errs[field.name] = nr.error;
                else if (!nr.skip) values[field.name] = nr.value;
              } else if (field.kind === "textarea" || field.kind === "text" || field.kind === "latin") {
                const input = session.fieldEls[field.name];
                const v = String(input.value || "").trim();
                if (!v && field.required !== false) errs[field.name] = field.err || "این فیلد الزامی است.";
                else values[field.name] = v;
              } else if (field.kind === "select") {
                values[field.name] = session.fieldEls[field.name].value;
              } else if (field.kind === "radio") {
                const checked = session.fieldEls[field.name].querySelector("input:checked");
                if (!checked) errs[field.name] = "یک گزینه را انتخاب کنید.";
                else values[field.name] = checked.value;
              } else if (field.kind === "checkbox") {
                values[field.name] = session.fieldEls[field.name].checked;
              } else if (field.kind === "switch") {
                values[field.name] = session.fieldEls[field.name].getAttribute("aria-checked") === "true";
              } else if (field.kind === "custom" && typeof field.read === "function") {
                const r = field.read(session);
                if (r && r.errors) Object.assign(errs, r.errors);
                if (r && r.values) Object.assign(values, r.values);
              }
            }
            if (Object.keys(errs).length) {
              session._applyFieldErrors(errs);
              return;
            }
            if (spec.validate) {
              const v = spec.validate(values);
              if (v) {
                session._applyFieldErrors(v);
                return;
              }
            }
            await session.submit(values);
          };

          render();
          if (typeof spec.afterRender === "function") spec.afterRender(session);
        }),
    );
  }

  window.cpSetFieldDisabled = setCpFieldDisabled;

  async function cpForm(opts) {
    return openForm(opts);
  }

  async function cpPrompt(opts) {
    const field = { ...(opts.field || {}), name: "value" };
    const { onSubmit, ...rest } = opts;
    return cpForm({
      ...rest,
      fields: [field],
      onSubmit:
        typeof onSubmit === "function"
          ? async (values) => onSubmit(values.value)
          : undefined,
    }).then((v) => (v == null ? null : v.value));
  }

  async function cpConfirm(opts) {
    return enqueueOpen(
      () =>
        new Promise((resolve) => {
          const session = new LayerSession(
            {
              ...opts,
              role: "alertdialog",
              fields: [],
              collectAndSubmit: () => {},
            },
            (v) => resolve(v === true),
          );
          cpLayer.active = session;
          const ui = renderShell(session);
          const ok = document.createElement("button");
          ok.type = "button";
          ok.className = `cp-btn ${opts.tone === "danger" ? "cp-danger" : "cp-primary"}`;
          ok.textContent = opts.confirmLabel || "تأیید";
          ok.onclick = async () => {
            if (typeof opts.onConfirm === "function") {
              session.busy = true;
              session._setBusyUi(true);
              try {
                await opts.onConfirm();
                session._finish(true);
              } catch (e) {
                session.setServerError(cpTranslateApiError(e.code, e.message));
              } finally {
                session.busy = false;
                session._setBusyUi(false);
              }
            } else session._finish(true);
          };
          ui.foot.prepend(ok);
          ui.cancelBtn.onclick = () => {
            session._finish(false);
          };
          ui.sheet.focus({ preventScroll: true });
          if (opts.focusCancelFirst !== false) ui.cancelBtn.focus({ preventScroll: true });
          else ok.focus({ preventScroll: true });
        }),
    );
  }

  async function cpPick(opts) {
    return enqueueOpen(
      () =>
        new Promise((resolve) => {
          const session = new LayerSession({ ...opts, fields: [] }, resolve);
          cpLayer.active = session;
          const ui = renderShell(session);
          const host = ui.fieldsHost;
          host.className = "cp-pick-list";
          if (!opts.items?.length) {
            const empty = document.createElement("p");
            empty.className = "cp-empty";
            empty.textContent = opts.emptyText || "موردی نیست.";
            host.appendChild(empty);
          } else {
            for (const item of opts.items) {
              const btn = document.createElement("button");
              btn.type = "button";
              btn.className = "cp-pick-row";
              const titleEl = document.createElement("strong");
              titleEl.textContent = item.title ?? "";
              btn.appendChild(titleEl);
              if (item.subtitle) {
                const sub = document.createElement("span");
                sub.textContent = item.subtitle;
                btn.appendChild(sub);
              }
              btn.onclick = () => session._finish(item);
              host.appendChild(btn);
            }
          }
          ui.sheet.focus({ preventScroll: true });
        }),
    );
  }

  async function cpAlert(opts) {
    await enqueueOpen(
      () =>
        new Promise((resolve) => {
          const session = new LayerSession({ ...opts, fields: [] }, () => resolve());
          cpLayer.active = session;
          const ui = renderShell(session);
          const host = ui.fieldsHost;
          if (opts.codeDisplay) {
            const code = document.createElement("div");
            code.className = "cp-code-display";
            code.dir = "ltr";
            code.textContent = opts.codeDisplay;
            host.appendChild(code);
          }
          if (opts.rows?.length) {
            for (const row of opts.rows) {
              const div = document.createElement("div");
              div.className = "cp-alert-row";
              div.textContent = `${row.title}${row.subtitle ? " — " + row.subtitle : ""}`;
              host.appendChild(div);
            }
          } else if (opts.emptyText) {
            host.textContent = opts.emptyText;
          }
          ui.foot.replaceChildren();
          const close = document.createElement("button");
          close.type = "button";
          close.className = "cp-btn cp-primary";
          close.textContent = opts.okLabel || "ثبت";
          close.onclick = () => session._finish(undefined);
          ui.foot.appendChild(close);
          ui.sheet.focus({ preventScroll: true });
          close.focus({ preventScroll: true });
        }),
    );
  }

  window.cpLayer = cpLayer;
  window.cpParseNumber = cpParseNumber;
  window.cpParseNumberResult = cpParseNumberResult;
  window.cpParseInteger = cpParseInteger;
  window.cpParseIntegerResult = cpParseIntegerResult;
  window.cpTranslateApiError = cpTranslateApiError;
  window.cpForm = cpForm;
  window.cpPrompt = cpPrompt;
  window.cpConfirm = cpConfirm;
  window.cpPick = cpPick;
  window.cpAlert = cpAlert;
  window.cpToast = cpToast;
})();