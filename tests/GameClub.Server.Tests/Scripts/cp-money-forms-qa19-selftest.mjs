import vm from "node:vm";
import {
  loadOperatorApp,
  installFetchCapture,
  sleep,
  fillMoneyField,
  fillNumberField,
  fillCpPromptValue,
  clickCpSubmit,
  waitCpHidden,
  waitCpVisible,
  lastPost,
  assertNumberFields,
} from "./operator-jsdom-harness.mjs";

const CUSTOMER = "11111111-1111-4111-8111-111111111111";
const SESSION = "22222222-2222-4222-8222-222222222222";
const TOPUP_GROUP = "33333333-3333-4333-8333-333333333333";

const results = [];

function baseStubs(cap) {
  cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet`, "GET", {
    realToman: 0,
    giftToman: 0,
    spendableToman: 0,
  });
  cap.stub("/api/operator/shift/state", "GET", { shiftsEnabled: true, openShift: null });
  cap.stub("/api/operator/destination-cards", "GET", [{ id: "card-1" }]);
  cap.stub("/api/operator/grid", "GET", [{ id: "ps5-1", sessionId: SESSION, status: "Playing" }]);
  cap.stub(`/api/operator/sessions/${SESSION}/prepaid`, "GET", {
    prepaidKind: null,
    prepaidEndAlert: false,
  });
  cap.stub(`/api/operator/sessions/${SESSION}/sale`, "GET", { linkedCustomer: null, lines: [] });
}

function linkCustomer(runInApp) {
  runInApp(`displayedLinkedCustomer = { id: "${CUSTOMER}", name: "QA عضو" };`);
}

function selectSession(runInApp) {
  runInApp(
    `selectedSessionId = "${SESSION}"; lastGridTiles = [{ sessionId: "${SESSION}", id: "ps5-1" }]; destinationCards = [{ id: "card-1" }];`,
  );
  cap_stub_grid(runInApp);
}

function cap_stub_grid(runInApp) {
  runInApp("lastGridTiles = lastGridTiles || [];");
}

async function submitMoneyForm(document) {
  await clickCpSubmit(document);
  await sleep(80);
  await waitCpHidden(document, 8000);
}

async function runCase(qaRow, formId, open, fill, endpoint, expectedBody) {
  const { window, document, runInApp, ctx } = loadOperatorApp();
  const cap = installFetchCapture(window, ctx);
  baseStubs(cap);
  linkCustomer(runInApp);
  selectSession(runInApp);

  await Promise.resolve(open({ window, document, cap }));
  await waitCpVisible(document);
  await fill(document);
  cap.reset();
  await submitMoneyForm(document);
  const httpMethod = endpoint.includes("debt-floor") || endpoint.includes("birthday") || endpoint.includes("gift-cap") || endpoint.includes("level-ladder")
    ? "PUT"
    : "POST";
  const post = lastPost(cap.log, endpoint, httpMethod);
  if (!post) throw new Error(`QA#${qaRow} ${formId}: no ${httpMethod} to ${endpoint}`);
  assertNumberFields(post.body, expectedBody);
  results.push({ qaRow, formId, endpoint, body: expectedBody });
}

async function main() {
  await runCase(
    1,
    "F01 top-up cash",
    async () => {
      void globalThis.walletTopUp("Cash");
    },
    async (document) => {
      await fillMoneyField(document, "amountToman", "12345");
      await fillMoneyField(document, "giftToman", "678");
    },
    "/wallet/top-up",
    { amountToman: 12345, giftToman: 678 },
  );

  await runCase(
    2,
    "F02 card reader",
    async () => {
      void globalThis.walletTopUp("CardReader");
    },
    async (document) => {
      await fillMoneyField(document, "amountToman", "20000");
    },
    "/wallet/top-up",
    { amountToman: 20000, giftToman: 0 },
  );

  await runCase(
    3,
    "F03 card-to-card",
    async ({ cap }) => {
      cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet`, "GET", {
        realToman: 0,
        giftToman: 0,
        spendableToman: 0,
      });
      void globalThis.walletTopUp("CardToCard");
    },
    async (document) => {
      await fillMoneyField(document, "amountToman", "30000");
    },
    "/wallet/top-up",
    { amountToman: 30000, giftToman: 0 },
  );

  {
    const { window, document, runInApp, ctx } = loadOperatorApp();
    const cap = installFetchCapture(window, ctx);
    baseStubs(cap);
    linkCustomer(runInApp);
    cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet/top-ups`, "GET", [
      {
        topUpGroupId: TOPUP_GROUP,
        method: "Cash",
        currentAmountToman: 20000,
        currentGiftToman: 0,
        atUtc: "2025-01-01T00:00:00Z",
      },
    ]);
    void globalThis.walletCorrectTopUp();
    await waitCpVisible(document, 8000);
    document.querySelector(".cp-pick-row")?.click();
    await waitCpVisible(document);
    await fillMoneyField(document, "newAmountToman", "45000");
    await fillMoneyField(document, "newGiftToman", "0");
    const desc = document.querySelector('[data-cp-field="description"]');
    if (desc) desc.value = "اصلاح تست";
    cap.reset();
    await submitMoneyForm(document);
    const post = lastPost(cap.log, "/correct");
    assertNumberFields(post.body, { newAmountToman: 45000, newGiftToman: 0 });
    results.push({ qaRow: 4, formId: "F04 correction", endpoint: post.url, body: { newAmountToman: 45000, newGiftToman: 0 } });
  }

  await runCase(
    5,
    "F09 manager gift",
    async ({ document }) => {
      document.getElementById("mgrGiftBtn")?.click();
    },
    async (document) => {
      await fillCpPromptValue(document, "2222");
    },
    "/wallet/gift",
    { giftToman: 2222 },
  );

  await runCase(
    6,
    "F08 prepaid amount",
    async ({ document }) => {
      document.getElementById("prepaidAmountBtn")?.click();
    },
    async (document) => {
      await fillCpPromptValue(document, "55000");
    },
    "/prepaid/amount",
    { amountToman: 55000 },
  );

  await runCase(
    7,
    "F07 prepaid duration",
    async ({ document }) => {
      document.getElementById("prepaidDurationBtn")?.click();
    },
    async (document) => {
      await fillCpPromptValue(document, "60");
    },
    "/prepaid/duration",
    { minutes: 60 },
  );

  await runCase(
    8,
    "F08 opening real+gift",
    async ({ document }) => {
      document.getElementById("mgrOpeningBtn")?.click();
    },
    async (document) => {
      await fillMoneyField(document, "realToman", "17500");
      await fillMoneyField(document, "giftToman", "2500");
    },
    "/opening-balance",
    { realToman: 17500, giftToman: 2500 },
  );

  await runCase(
    "8b",
    "F08 opening real+gift+debt0",
    async ({ document }) => {
      document.getElementById("mgrOpeningBtn")?.click();
    },
    async (document) => {
      await fillMoneyField(document, "realToman", "10000");
      await fillMoneyField(document, "giftToman", "0");
      await fillMoneyField(document, "debtToman", "0");
    },
    "/opening-balance",
    { realToman: 10000, giftToman: 0, debtToman: 0 },
  );

  await runCase(
    9,
    "F08 opening debt",
    async ({ document }) => {
      document.getElementById("mgrOpeningBtn")?.click();
    },
    async (document) => {
      await fillMoneyField(document, "debtToman", "30000");
    },
    "/opening-balance",
    { debtToman: 30000 },
  );

  await runCase(
    10,
    "F01 debt payment",
    async ({ cap }) => {
      cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet`, "GET", {
        realToman: -30000,
        giftToman: 0,
        spendableToman: -30000,
      });
      void globalThis.walletTopUp("Cash");
    },
    async (document) => {
      await fillMoneyField(document, "amountToman", "30000");
    },
    "/wallet/top-up",
    { amountToman: 30000, giftToman: 0 },
  );

  await runCase(
    11,
    "F11 debt floor",
    async ({ document, cap }) => {
      cap.stub("/api/operator/members/settings/debt-floor", "GET", { floorToman: null });
      document.getElementById("mgrFloorBtn")?.click();
    },
    async (document) => {
      const limited = document.querySelector('input[type="radio"][value="limited"]');
      if (limited) {
        limited.checked = true;
        limited.dispatchEvent(new window.Event("change", { bubbles: true }));
      }
      await fillMoneyField(document, "maxDebt", "80000");
    },
    "/debt-floor",
    { floorToman: -80000 },
  );

  await runCase(
    12,
    "F12 gift cap",
    async ({ document, cap }) => {
      cap.stub("/api/operator/members/settings/operator-gift-cap", "GET", { maxToman: 0, maxPercent: 0 });
      document.getElementById("mgrGiftCapBtn")?.click();
    },
    async (document) => {
      await fillMoneyField(document, "maxToman", "30000");
      await fillNumberField(document, "maxPercent", "20");
    },
    "operator-gift-cap",
    { maxToman: 30000, maxPercent: 20 },
  );

  await runCase(
    13,
    "F12 birthday",
    async ({ document, cap }) => {
      cap.stub("/api/operator/members/settings/birthday", "GET", {
        percent: 10,
        windowKind: "day_window",
        windowDays: 3,
        anchor: "around",
      });
      document.getElementById("mgrBirthdayBtn")?.click();
    },
    async (document) => {
      await fillNumberField(document, "percent", "15");
      const dayWindow = document.querySelector('input[value="day_window"]');
      dayWindow?.click();
      await fillNumberField(document, "windowDays", "3");
    },
    "/settings/birthday",
    { percent: 15, windowDays: 3 },
  );

  {
    const { window, document, runInApp, ctx } = loadOperatorApp();
    const cap = installFetchCapture(window, ctx);
    baseStubs(cap);
    cap.stub("/api/operator/members/settings/level-ladder", "GET", []);
    document.getElementById("mgrLadderBtn")?.click();
    await waitCpVisible(document);
    await sleep(400);
    document.querySelector(".cp-ladder-body .cp-btn.cp-secondary")?.click();
    await sleep(40);
    const hours = document.querySelector('[data-cp-ladder-field="ladder:0:hours"]');
    const pct = document.querySelector('[data-cp-ladder-field="ladder:0:percent"]');
    const name = document.querySelector('[data-cp-ladder-field="ladder:0:name"]');
    if (hours) hours.value = "0";
    if (pct) pct.value = "12";
    if (name) name.value = "L1";
    cap.reset();
    await submitMoneyForm(document);
    const post = lastPost(cap.log, "level-ladder", "PUT");
  const row = Array.isArray(post?.body) ? post.body[0] : post?.body?.[0];
    if (!row) throw new Error("F14 ladder body missing");
    if (typeof row.minimumLifetimeHours !== "number" || row.minimumLifetimeHours !== 0) throw new Error("ladder hours");
    if (typeof row.discountPercent !== "number" || row.discountPercent !== 12) throw new Error("ladder percent");
    results.push({ qaRow: 14, formId: "F14 ladder", endpoint: post.url, body: { minimumLifetimeHours: 0, discountPercent: 12 } });
  }

  {
    const { window, document, runInApp, ctx } = loadOperatorApp();
    const cap = installFetchCapture(window, ctx);
    baseStubs(cap);
    selectSession(runInApp);
    runInApp(
      `catalog = [{ name: "Food", items: [{ id: 42, kind: "ServiceCustom", name: "خدمت دلخواه" }] }];`,
    );
    globalThis.renderBuffetCategories();
    document.querySelector(".cat-item")?.click();
    await waitCpVisible(document);
    await fillCpPromptValue(document, "12345");
    cap.reset();
    await submitMoneyForm(document);
    const post = lastPost(cap.log, "/sale/lines");
    assertNumberFields(post.body, { customAmountToman: 12345 });
    results.push({ qaRow: 15, formId: "F05 custom service", endpoint: post.url, body: { customAmountToman: 12345 } });
  }

  {
    const { window, document, runInApp, ctx } = loadOperatorApp();
    const cap = installFetchCapture(window, ctx);
    baseStubs(cap);
    selectSession(runInApp);
    cap.stub(`/api/operator/sessions/${SESSION}/bill/open`, "POST", {
      collectibleToman: 20000,
      unroundedPayableToman: 20000,
    });
    cap.stub(`/api/operator/sessions/${SESSION}/bill`, "GET", { collectibleToman: 20000 });
    cap.stub(`/api/operator/sessions/${SESSION}/bill/confirm`, "POST", { ok: true });
    document.getElementById("openBillBtn")?.click();
    await sleep(60);
    document.getElementById("addCashLineBtn")?.click();
    await waitCpVisible(document);
    await fillCpPromptValue(document, "5000");
    await submitMoneyForm(document);
    document.getElementById("addCardLineBtn")?.click();
    await waitCpVisible(document);
    await fillCpPromptValue(document, "15000");
    await submitMoneyForm(document);
    cap.reset();
    document.getElementById("confirmBtn")?.click();
    await sleep(100);
    const post = lastPost(cap.log, "/bill/confirm");
    const payments = post?.body?.payments;
    if (!Array.isArray(payments) || payments.length !== 2) throw new Error("bill payments");
    const cash = payments.find((p) => p.method === "Cash");
    const card = payments.find((p) => p.method === "CardReader");
    assertNumberFields(cash, { amountToman: 5000 });
    assertNumberFields(card, { amountToman: 15000 });
    results.push({ qaRow: 16, formId: "F16 bill pay", endpoint: post.url, body: { cash: 5000, card: 15000 } });
  }

  {
    const { window, document, runInApp, ctx } = loadOperatorApp();
    const cap = installFetchCapture(window, ctx);
    baseStubs(cap);
    cap.stub("/api/operator/game-titles", "GET", []);
    cap.stub("/api/operator/game-titles", "POST", { id: 1, name: "FIFA", enabled: true, rates: [] });
    cap.stub("/api/operator/game-titles/console-options", "GET", [{ mode: "Console", playerCount: 1 }]);
    runInApp(
      `ss14EnsureManagerPanel(); ss14MgrSelectedId = null; ss14MgrConsoleKeys = [{ mode: "Console", playerCount: 1 }]; ss14RenderManagerForm({ name: "FIFA", enabled: true, rates: [] });`,
    );
    const hourly = document.querySelector('input[data-rate="hourly"]');
    if (hourly) hourly.value = "120000";
    await globalThis.ss14SaveManagerTitle();
    await sleep(150);
    const post = lastPost(cap.log, "/game-titles", "POST") || lastPost(cap.log, "/game-titles/", "PUT");
    const rate = post?.body?.rates?.[0];
    if (typeof rate?.hourlyToman !== "number" || rate.hourlyToman !== 120000) throw new Error(`game rate body ${JSON.stringify(post?.body)}`);
    results.push({ qaRow: 17, formId: "F17 game rate", endpoint: post.url, body: { hourlyToman: 120000 } });
  }

  await runCase(
    18,
    "F17 shift open",
    async ({ document }) => {
      document.getElementById("openShiftBtn")?.click();
    },
    async (document) => {
      await fillCpPromptValue(document, "200000");
    },
    "/shift/open",
    { openingFloatToman: 200000 },
  );

  {
    const { window, document, runInApp, ctx } = loadOperatorApp();
    const cap = installFetchCapture(window, ctx);
    baseStubs(cap);
    cap.stub("/api/operator/shift/close/preview", "GET", {
      openSessions: [],
      expected: { cashExpected: 0, cardReaderExpected: 0 },
    });
    cap.stub("/api/operator/shift/close", "POST", { ok: true });
    cap.stub("/api/operator/shift/jalali/1403/12/1", "GET", { year: 2025, month: 3, day: 1 });
    document.getElementById("closeCash").value = "250000";
    document.getElementById("closeReader").value = "0";
    document.getElementById("closeJy").value = "1403";
    document.getElementById("closeJm").value = "12";
    document.getElementById("closeJd").value = "1";
    cap.reset();
    document.getElementById("closeShiftBtn")?.click();
    await sleep(120);
    const post = lastPost(cap.log, "/shift/close");
    assertNumberFields(post.body, { actualCashToman: 250000, actualCardReaderToman: 0 });
    results.push({ qaRow: 19, formId: "F19 shift close", endpoint: post.url, body: { actualCashToman: 250000, actualCardReaderToman: 0 } });
  }

  for (const r of results) {
    console.log(`QA${r.qaRow}\t${r.formId}\t${r.endpoint}\t${JSON.stringify(r.body)}`);
  }
  if (results.length !== 20) throw new Error(`expected 20 form results, got ${results.length}`);
  console.log("cp-money-forms-qa19-selftest: ok");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});