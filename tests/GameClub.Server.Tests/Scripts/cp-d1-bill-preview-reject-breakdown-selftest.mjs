import {
  loadOperatorApp,
  installFetchCapture,
  captureBillPreviewDebounce,
  waitCpVisible,
} from "./operator-jsdom-harness.mjs";

const SESSION = "33333333-3333-4333-8333-333333333333";

async function waitForCondition(label, predicate, { timeout = 4000, interval = 20 } = {}) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    if (predicate()) return;
    await new Promise((r) => setTimeout(r, interval));
  }
  throw new Error(`timeout waiting for ${label}`);
}

const { window, document, runInApp, ctx } = loadOperatorApp();
const debounce = captureBillPreviewDebounce(ctx);
const cap = installFetchCapture(window, ctx);

cap.stubJsonError(`/api/operator/sessions/${SESSION}/bill/preview`, "POST", 400, {
  code: "payment_invalid",
  message: "مبلغ قابل پرداخت ۱۵٬۰۰۰ تومان است",
  collectibleToman: 15_000,
  unroundedPayableToman: 14_000,
  roundingToman: 1_000,
  tenderedToman: 5_000,
  remainingToman: 10_000,
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

await waitForCondition("bill preview rejection message", () => {
  const errText = (document.getElementById("error")?.textContent || "").trim();
  return errText.includes("۱۵٬۰۰۰");
});

const detail = (document.getElementById("billDetail")?.textContent || "").trim();
if (!detail.includes("15000") && !detail.includes("۱۵٬۰۰۰")) {
  throw new Error("billDetail must retain collectible breakdown after preview rejection");
}

const change = (document.getElementById("cashChange")?.textContent || "").trim();
if (!change.includes("۱۰٬۰۰۰") && !change.includes("10000")) {
  throw new Error("cashChange should show remaining after preview rejection");
}

console.log("cp-d1-bill-preview-reject-breakdown-selftest: ok");