import {
  loadOperatorApp,
  installFetchCapture,
  sleep,
} from "./operator-jsdom-harness.mjs";

const SESSION = "33333333-3333-4333-8333-333333333333";

const { window, document, runInApp, ctx } = loadOperatorApp();
const cap = installFetchCapture(window, ctx);

cap.stub(`/api/operator/sessions/${SESSION}/bill/preview`, "POST", {
  unroundedPayableToman: 17_000,
  roundingToman: 3_000,
  collectibleToman: 20_000,
  timeGrossToman: 2_000,
  buffetGrossToman: 15_000,
  servicesGrossToman: 0,
  saleLines: [],
  segments: [],
  walletShortRemainder: null,
});

runInApp(`
  billTarget = "session";
  selectedSessionId = "${SESSION}";
  paymentDraft = [{ method: "Cash", amountToman: 20000 }];
  document.getElementById("bill")?.classList.remove("hidden");
  renderBill({
    unroundedPayableToman: 17000,
    roundingToman: 3000,
    collectibleToman: 20000,
    timeGrossToman: 2000,
    buffetGrossToman: 15000,
    servicesGrossToman: 0,
    saleLines: [],
    segments: [],
  });
  renderPaymentLines();
`);

const changeEl = document.getElementById("cashChange");
const changeText = (changeEl?.textContent || "").trim();
if (changeText.includes("۳٬۰۰۰") || changeText.includes("3,000")) {
  throw new Error(`KS1 UI must not show bogus change when collectible matches cash (got "${changeText}")`);
}
if (!changeText.includes("۲۰٬۰۰۰") && !changeText.includes("20000")) {
  throw new Error(`expected cash line summary for full pay (got "${changeText}")`);
}

console.log("cp-r14-ks1-preview-change-selftest: ok");