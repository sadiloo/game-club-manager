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
const BUFFET = "44444444-4444-4444-8444-444444444444";
const PREVIEW_TOKEN_LINE =
  "previewCollectibleToman: lastCollectibleToman > 0 ? lastCollectibleToman : collectible,";

async function waitForCondition(label, predicate, { timeout = 4000, interval = 20 } = {}) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    if (predicate()) return;
    await new Promise((r) => setTimeout(r, interval));
  }
  throw new Error(`timeout waiting for ${label}`);
}

function removePreviewTokenFromConfirmBlock(appSource, urlMarker) {
  const anchor = appSource.indexOf(urlMarker);
  if (anchor < 0) {
    throw new Error(`confirm marker not found: ${urlMarker}`);
  }
  const tail = appSource.slice(anchor);
  const lineIdx = tail.indexOf(PREVIEW_TOKEN_LINE);
  if (lineIdx < 0) {
    throw new Error(`previewCollectibleToman line missing after ${urlMarker}`);
  }
  const abs = anchor + lineIdx;
  return (
    appSource.slice(0, abs) +
    "paymentsOnly: true," +
    appSource.slice(abs + PREVIEW_TOKEN_LINE.length)
  );
}

function removeBuffetConfirmPreviewToken(appSource) {
  return removePreviewTokenFromConfirmBlock(appSource, "buffet-only/${buffetOpenSaleId}/confirm");
}

function removeSessionConfirmPreviewToken(appSource) {
  return removePreviewTokenFromConfirmBlock(appSource, "sessions/${selectedSessionId}/bill/confirm");
}

async function assertSessionConfirmSendsPreviewToken(appSource) {
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
  await waitForCondition("confirm POST", () => cap.log.some((e) => e.method === "POST" && String(e.url).includes("/sessions/") && String(e.url).includes("/bill/confirm")));
  const confirmPost = cap.log.find((e) => e.method === "POST" && String(e.url).includes("/sessions/") && String(e.url).includes("/bill/confirm"));
  return typeof confirmPost.body === "string" ? JSON.parse(confirmPost.body) : confirmPost.body;
}

async function assertBuffetConfirmSendsPreviewToken(appSource) {
  const { window, document, runInApp, ctx } = loadOperatorApp("http://localhost/operator/", { appSource });
  const debounce = captureBillPreviewDebounce(ctx);
  const cap = installFetchCapture(window, ctx);
  const previewBill = {
    collectibleToman: 15_000,
    unroundedPayableToman: 15_000,
    roundingToman: 0,
    timeGrossToman: 0,
    buffetGrossToman: 15_000,
    servicesGrossToman: 0,
    walletShortRemainder: null,
  };
  cap.stub(`/api/operator/buffet-only/${BUFFET}/bill/preview`, "POST", { bill: previewBill, lines: [] });
  runInApp(`
    billTarget = "buffet-only";
    buffetOpenSaleId = "${BUFFET}";
    paymentDraft = [{ method: "Cash", amountToman: 20000 }];
    billPreviewDraftKey = "";
    document.getElementById("bill")?.classList.remove("hidden");
    scheduleBillPreview();
  `);
  debounce.flush();
  await waitForCondition("buffet preview finished", () => runInApp("lastCollectibleToman === 15000"));
  cap.stub(`/api/operator/buffet-only/${BUFFET}`, "GET", { bill: { collectibleToman: 15_000 }, lines: [] });
  cap.stub(`/api/operator/buffet-only/${BUFFET}/confirm`, "POST", { bill: { collectibleToman: 15_000, changeToman: 5000 } });
  cap.stub("/api/operator/grid", "GET", []);
  cap.stub("/api/operator/low-stock", "GET", []);
  cap.stub("/api/operator/pending-card-to-card", "GET", []);
  document.getElementById("confirmBtn")?.click();
  await waitForCondition("buffet confirm POST", () =>
    cap.log.some((e) => e.method === "POST" && String(e.url).includes(`/buffet-only/${BUFFET}/confirm`)),
  );
  const confirmPost = cap.log.find((e) => e.method === "POST" && String(e.url).includes(`/buffet-only/${BUFFET}/confirm`));
  return typeof confirmPost.body === "string" ? JSON.parse(confirmPost.body) : confirmPost.body;
}

const sessionBody = await assertSessionConfirmSendsPreviewToken(headApp);
if (sessionBody.previewCollectibleToman !== 15_000) {
  throw new Error(`session confirm must send previewCollectibleToman=15000 (got ${sessionBody.previewCollectibleToman})`);
}

const buffetBody = await assertBuffetConfirmSendsPreviewToken(headApp);
if (buffetBody.previewCollectibleToman !== 15_000) {
  throw new Error(`buffet confirm must send previewCollectibleToman=15000 (got ${buffetBody.previewCollectibleToman})`);
}

const sessionMutant = removeSessionConfirmPreviewToken(headApp);
const sessionMutantBody = await assertSessionConfirmSendsPreviewToken(sessionMutant);
if (sessionMutantBody.previewCollectibleToman === 15_000) {
  throw new Error("session confirm without previewCollectibleToman must not send preview token");
}

const buffetStillOk = await assertBuffetConfirmSendsPreviewToken(sessionMutant);
if (buffetStillOk.previewCollectibleToman !== 15_000) {
  throw new Error("session-only mutant must not strip buffet confirm previewCollectibleToman");
}

const buffetMutant = removeBuffetConfirmPreviewToken(headApp);
const buffetMutantBody = await assertBuffetConfirmSendsPreviewToken(buffetMutant);
if (buffetMutantBody.previewCollectibleToman === 15_000) {
  throw new Error("buffet confirm without previewCollectibleToman must not send preview token");
}

const sessionStillOk = await assertSessionConfirmSendsPreviewToken(buffetMutant);
if (sessionStillOk.previewCollectibleToman !== 15_000) {
  throw new Error("buffet-only mutant must not strip session confirm previewCollectibleToman");
}

console.log("cp-d1-bill-confirm-preview-token-selftest: ok");