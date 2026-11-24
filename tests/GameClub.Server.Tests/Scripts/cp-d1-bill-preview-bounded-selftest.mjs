import {
  loadOperatorApp,
  installFetchCapture,
  captureBillPreviewDebounce,
  waitCpVisible,
} from "./operator-jsdom-harness.mjs";

const SESSION = "22222222-2222-4222-8222-222222222222";

function previewPostCount(log) {
  return log.filter(
    (e) => e.method === "POST" && String(e.url).includes(`/sessions/${SESSION}/bill/preview`),
  ).length;
}

async function waitForCondition(label, predicate, { timeout = 4000, interval = 20 } = {}) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    if (predicate()) return;
    await new Promise((r) => setTimeout(r, interval));
  }
  throw new Error(`timeout waiting for ${label}`);
}

const { window, document, runInApp, ctx } = loadOperatorApp();
const debounce = captureBillPreviewDebounce(window);
const cap = installFetchCapture(window, ctx);

cap.stub(`/api/operator/sessions/${SESSION}/bill/preview`, "POST", {
  collectibleToman: 15_000,
  unroundedPayableToman: 15_000,
  roundingToman: 0,
  timeGrossToman: 0,
  buffetGrossToman: 0,
  servicesGrossToman: 0,
  saleLines: [],
  segments: [],
  walletShortRemainder: null,
});

runInApp(`
  billTarget = "session";
  selectedSessionId = "${SESSION}";
  paymentDraft = [{ method: "Cash", amountToman: 5000 }];
  billPreviewDraftKey = "";
  document.getElementById("bill")?.classList.remove("hidden");
  scheduleBillPreview();
`);

debounce.flush();

await waitForCondition("first preview POST", () => previewPostCount(cap.log) >= 1);

let lastCount = previewPostCount(cap.log);
await waitForCondition("preview POSTs settled after idle", () => {
  const c = previewPostCount(cap.log);
  if (c === lastCount && c >= 1 && c <= 2) return true;
  lastCount = c;
  return false;
});

const idlePosts = previewPostCount(cap.log);
if (idlePosts > 2) {
  throw new Error(`preview POSTs must stay bounded after idle (got ${idlePosts})`);
}

cap.stub(`/api/operator/sessions/${SESSION}/bill`, "GET", {
  collectibleToman: 15_000,
  unroundedPayableToman: 15_000,
  roundingToman: 0,
});
cap.stubJsonError(`/api/operator/sessions/${SESSION}/bill/confirm`, "POST", 400, {
  code: "payment_invalid",
  message: "مبلغ قابل پرداخت ۱۵٬۰۰۰ تومان است",
});

const confirmBtn = document.getElementById("confirmBtn");
if (!confirmBtn) throw new Error("missing confirmBtn");
confirmBtn.onclick();
await waitForCondition("confirm error visible", () => {
  const errText = (document.getElementById("error")?.textContent || "").trim();
  return errText.includes("۱۵٬۰۰۰");
});

const afterRejectPosts = previewPostCount(cap.log);
if (afterRejectPosts > idlePosts + 1) {
  throw new Error("preview loop must not fire after confirm rejection");
}

console.log("cp-d1-bill-preview-bounded-selftest: ok");