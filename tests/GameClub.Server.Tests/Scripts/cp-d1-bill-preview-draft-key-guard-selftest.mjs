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

const { window, document, runInApp, ctx } = loadOperatorApp();
const debounce = captureBillPreviewDebounce(ctx);
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

cap.reset();

let debounceCalls = 0;
const nativeCtxSetTimeout = ctx.setTimeout;
ctx.setTimeout = (fn, ms, ...rest) => {
  if (ms === 200) debounceCalls += 1;
  return nativeCtxSetTimeout(fn, ms, ...rest);
};

runInApp(`
  for (let i = 0; i < 8; i++) scheduleBillPreview();
`);

if (debounceCalls > 0) {
  throw new Error(
    `scheduleBillPreview must return before debounce when draft key unchanged (setTimeout(200) called ${debounceCalls} times)`,
  );
}

debounce.restore();
ctx.setTimeout = nativeCtxSetTimeout;
debounce.flush();

if (previewPostCount(cap.log) > 0) {
  throw new Error("no preview POST when payment draft unchanged");
}

console.log("cp-d1-bill-preview-draft-key-guard-selftest: ok");