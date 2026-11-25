import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  loadOperatorApp,
  installFetchCapture,
  waitCpVisible,
  waitCpHidden,
  fillMoneyField,
  clickCpSubmit,
} from "./operator-jsdom-harness.mjs";

const CUSTOMER = "11111111-1111-4111-8111-111111111111";
const TOPUP_GROUP = "33333333-3333-4333-8333-333333333333";
const here = path.dirname(fileURLToPath(import.meta.url));

function assertOnOperator(window) {
  if (!String(window.location.href).includes("/operator/")) {
    throw new Error(`left operator: ${window.location.href}`);
  }
}

function dispatchStalePopstate(window) {
  const PopState = window.PopStateEvent;
  window.dispatchEvent(new PopState("popstate", { state: { page: "start" } }));
}

async function waitFor(predicate, label, timeout = 4000) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    if (predicate()) return;
    await new Promise((r) => setTimeout(r, 5));
  }
  throw new Error(`timeout: ${label}`);
}

async function runStalePopstateScenario(delayMs, layerSource) {
  const { window, document, history, runInApp, ctx } = loadOperatorApp("http://localhost/operator/", {
    trackKeydown: false,
    layerSource,
  });

  history.pushState({ page: "start" }, "", "/start/");
  history.pushState({ page: "operator" }, "", "/operator/");
  assertOnOperator(window);

  const cap = installFetchCapture(window, ctx);
  cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet`, "GET", { realToman: 0, giftToman: 0, spendableToman: 0 });
  runInApp(`displayedLinkedCustomer = { id: "${CUSTOMER}", name: "T" };`);
  cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet/top-ups`, "GET", [
    {
      topUpGroupId: TOPUP_GROUP,
      method: "Cash",
      currentAmountToman: 1000,
      currentGiftToman: 0,
      atUtc: "2025-01-01T00:00:00Z",
    },
  ]);

  void globalThis.walletCorrectTopUp();
  await waitCpVisible(document);
  document.querySelector(".cp-pick-row")?.click();
  await waitCpVisible(document);

  const correctPath = `/api/operator/members/wallet/top-ups/${TOPUP_GROUP}/correct`;
  cap.stub(correctPath, "POST", {});

  await fillMoneyField(document, "newAmountToman", "2000");
  await fillMoneyField(document, "newGiftToman", "0");
  document.querySelector('[data-cp-field="description"]').value = "stale popstate";

  await clickCpSubmit(document);
  if (delayMs === 0) {
    dispatchStalePopstate(window);
  } else {
    await waitFor(() => runInApp("window.cpLayer._closingViaHistory === true"), "programmatic history close");
    await new Promise((resolve) => setTimeout(resolve, delayMs));
    dispatchStalePopstate(window);
  }
  assertOnOperator(window);

  await waitCpHidden(document);
  assertOnOperator(window);
  if (runInApp("window.cpLayer._historyPopWaiters.length > 0")) {
    throw new Error("correction form closed by a stale popstate / stale history entry");
  }
}

const headLayer = fs.readFileSync(
  path.join(here, "../../../src/GameClub.Server/wwwroot/operator/cp-layer.js"),
  "utf8",
);
const oldLayer = fs.readFileSync(path.join(here, "fixtures/cp-layer-pre-r14d-popstate.mjs"), "utf8");
const noBusyGuardLayer = fs.readFileSync(
  path.join(here, "fixtures/cp-layer-no-busy-popstate-guard.mjs"),
  "utf8",
);

for (const delay of [0, 30, 100]) {
  await runStalePopstateScenario(delay, headLayer);
}

let oldFailed = false;
let oldErr = "";
for (const delay of [0, 30, 100]) {
  try {
    await runStalePopstateScenario(delay, oldLayer);
  } catch (e) {
    oldFailed = true;
    oldErr = e?.message || String(e);
    break;
  }
}

async function runHb1BusyGuardScenario(layerSource, expectKeepOpen) {
  const { window, document, history, runInApp, ctx } = loadOperatorApp("http://localhost/operator/", {
    trackKeydown: false,
    layerSource,
  });
  history.pushState({ page: "start" }, "", "/start/");
  history.pushState({ page: "operator" }, "", "/operator/");
  const cap = installFetchCapture(window, ctx);
  cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet`, "GET", { realToman: 0, giftToman: 0, spendableToman: 0 });
  runInApp(`displayedLinkedCustomer = { id: "${CUSTOMER}", name: "T" };`);
  cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet/top-ups`, "GET", [
    { topUpGroupId: TOPUP_GROUP, method: "Cash", currentAmountToman: 1000, currentGiftToman: 0, atUtc: "2025-01-01T00:00:00Z" },
  ]);
  void globalThis.walletCorrectTopUp();
  await waitCpVisible(document);
  document.querySelector(".cp-pick-row")?.click();
  await waitCpVisible(document);
  const correctPath = `/api/operator/members/wallet/top-ups/${TOPUP_GROUP}/correct`;
  cap.hangingPost(correctPath);
  await fillMoneyField(document, "newAmountToman", "2000");
  await fillMoneyField(document, "newGiftToman", "0");
  document.querySelector('[data-cp-field="description"]').value = "stale popstate";
  await clickCpSubmit(document);
  await waitFor(() => runInApp("window.cpLayer.active?.busy === true"), "HB1 submit busy");
  dispatchStalePopstate(window);
  const stillBusy = runInApp("window.cpLayer.active?.busy === true");
  const root = document.getElementById("cpLayer");
  const hidden = !root || root.classList.contains("hidden");
  if (expectKeepOpen) {
    if (!stillBusy) throw new Error("HB1: submit must stay busy after stale popstate");
    if (hidden) throw new Error("HB1: layer hidden during busy submit after stale popstate");
  } else if (!hidden && stillBusy) {
    throw new Error("mutant busy-guard: stale popstate must cancel during busy submit");
  }
}

await runHb1BusyGuardScenario(headLayer, true);

if (!headLayer.includes("active.busy || active._submitInFlight")) {
  throw new Error("HB1: shipped cp-layer must ignore stale popstate while submit busy");
}

if (!noBusyGuardLayer.includes("mutant R14e-N2: busy guard removed")) {
  throw new Error("HB1 mutant fixture missing busy-guard removal");
}

if (!oldFailed) {
  throw new Error("expected failure on pre-r14d cp-layer stale popstate");
}

if (
  oldFailed
  && !oldErr.includes("left operator")
  && !oldErr.includes("stale popstate")
  && !oldErr.includes("timeout")
  && !oldErr.includes("history entry")
  && !oldErr.includes("undefined")
) {
  throw new Error(`unexpected old failure: ${oldErr}`);
}

console.log("cp-d4-deferred-popstate-correction-selftest: ok");