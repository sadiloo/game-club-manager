import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  loadOperatorApp,
  installFetchCapture,
  captureBillPreviewDebounce,
} from "./operator-jsdom-harness.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../src/GameClub.Server/wwwroot/operator");
const headApp = fs.readFileSync(path.join(root, "app.js"), "utf8");

const SESSION = "33333333-3333-4333-8333-333333333333";

async function waitForCondition(label, predicate, { timeout = 4000, interval = 20 } = {}) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    if (predicate()) return;
    await new Promise((r) => setTimeout(r, interval));
  }
  throw new Error(`timeout waiting for ${label}`);
}

async function assertConfirmSendsPreviewToken(appSource) {
  const { window, document, runInApp, ctx } = loadOperatorApp("http://localhost/operator/", { appSource });
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
    paymentDraft = [{ method: "Cash", amountToman: 20000 }];
    billPreviewDraftKey = "";
    document.getElementById("bill")?.classList.remove("hidden");
    scheduleBillPreview();
  `);
  debounce.flush();
  await waitForCondition("preview finished", () => runInApp("lastCollectibleToman === 15000"));
  cap.stub(`/api/operator/sessions/${SESSION}/bill`, "GET", { collectibleToman: 15_000 });
  cap.stub(`/api/operator/sessions/${SESSION}/bill/confirm`, "POST", { collectibleToman: 15_000, changeToman: 5000 });
  cap.stub("/api/operator/grid", "GET", []);
  cap.stub("/api/operator/low-stock", "GET", []);
  cap.stub("/api/operator/pending-card-to-card", "GET", []);
  document.getElementById("confirmBtn")?.click();
  await waitForCondition("confirm POST", () => cap.log.some((e) => e.method === "POST" && String(e.url).includes("/bill/confirm")));
  const confirmPost = cap.log.find((e) => e.method === "POST" && String(e.url).includes("/bill/confirm"));
  return typeof confirmPost.body === "string" ? JSON.parse(confirmPost.body) : confirmPost.body;
}

const body = await assertConfirmSendsPreviewToken(headApp);
if (body.previewCollectibleToman !== 15_000) {
  throw new Error(`confirm must send previewCollectibleToman=15000 (got ${body.previewCollectibleToman})`);
}

const mutantApp = headApp.replace(
  /previewCollectibleToman: lastCollectibleToman > 0 \? lastCollectibleToman : collectible,/g,
  "paymentsTokenOmitted: true,",
);
const mutantBody = await assertConfirmSendsPreviewToken(mutantApp);
if (mutantBody.previewCollectibleToman === 15_000) {
  throw new Error("mutant confirm without previewCollectibleToman must not send preview token");
}

console.log("cp-d1-bill-confirm-preview-token-selftest: ok");