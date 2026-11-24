import {
  loadOperatorApp,
  installFetchCapture,
  sleep,
} from "./operator-jsdom-harness.mjs";

const SESSION = "22222222-2222-4222-8222-222222222222";

function previewPostCount(log) {
  return log.filter(
    (e) => e.method === "POST" && String(e.url).includes(`/sessions/${SESSION}/bill/preview`),
  ).length;
}

const { window, document, runInApp, ctx } = loadOperatorApp();
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

cap.reset();
await sleep(2500);

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
await sleep(400);

const errEl = document.getElementById("error");
const errText = (errEl?.textContent || "").trim();
if (!errText.includes("۱۵٬۰۰۰")) {
  throw new Error(`rejected confirm must keep error visible (got "${errText}")`);
}

const afterRejectPosts = previewPostCount(cap.log);
if (afterRejectPosts > idlePosts + 1) {
  throw new Error("preview loop must not fire after confirm rejection");
}

console.log("cp-d1-bill-preview-bounded-selftest: ok");