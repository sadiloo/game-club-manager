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
  if (!runInApp("history.state && history.state.cpDialog")) {
    throw new Error("N2: correction form must push cpDialog history entry after pick handoff");
  }

  const correctPath = `/api/operator/members/wallet/top-ups/${TOPUP_GROUP}/correct`;
  cap.stub(correctPath, "POST", {});

  await fillMoneyField(document, "newAmountToman", "2000");
  await fillMoneyField(document, "newGiftToman", "0");
  document.querySelector('[data-cp-field="description"]').value = "stale popstate";

  await clickCpSubmit(document);
  await waitFor(
    () =>
      runInApp(
        "window.cpLayer._submitClosing === true || window.cpLayer._closingViaHistory === true || window.cpLayer.active == null",
      ),
    "programmatic history close",
  );
  if (delayMs > 0) {
    await new Promise((resolve) => setTimeout(resolve, delayMs));
  }
  dispatchStalePopstate(window);
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
const pickOnlyApp = "/* r14n pick-only pin: cp-layer only */";

async function runCpPickNoFollowingForm(layerSource, { expectHistoryClean = true } = {}) {
  const { window, document, history, runInApp } = loadOperatorApp("http://localhost/operator/", {
    trackKeydown: false,
    layerSource,
    appSource: pickOnlyApp,
  });
  history.pushState({ page: "start" }, "", "/start/");
  history.pushState({ page: "operator" }, "", "/operator/");
  assertOnOperator(window);
  const len0 = history.length;
  void window.cpPick({ title: "R14n pick-only", items: [{ id: "x", title: "pick" }] });
  await waitCpVisible(document);
  document.querySelector(".cp-pick-row")?.click();
  await waitCpHidden(document);
  await waitFor(
    () =>
      runInApp(
        expectHistoryClean
          ? "window.cpLayerIsSettled() && window.cpLayer._historyPopWaiters.length === 0 && !(history.state && history.state.cpDialog)"
          : "window.cpLayerIsSettled() && window.cpLayer._historyPopWaiters.length === 0",
      ),
    expectHistoryClean ? "pick-only clear cpDialog" : "pick-only layer settled",
  );
  const lenAfterPick = history.length;
  const hasCpDialog = !!(history.state && history.state.cpDialog);
  history.back();
  await waitFor(() => history.state?.page === "operator", "pick-only back to operator");
  return { len0, lenAfterPick, hasCpDialog, backOk: history.state?.page === "operator" };
}

const headAllHandoff = headLayer.replace(
  'session._finish(item, opts.handoffToNextLayer ? "handoff" : undefined)',
  'session._finish(item, "handoff")',
);

const pickOnlyClean = await runCpPickNoFollowingForm(headLayer);
if (pickOnlyClean.hasCpDialog) {
  throw new Error("R14n: pick without following form must not leave cpDialog state after pick");
}
if (!pickOnlyClean.backOk) {
  throw new Error("R14n: pick without following form must allow one back to operator history");
}
const pickOnlyMutant = await runCpPickNoFollowingForm(headAllHandoff, { expectHistoryClean: false });
if (!pickOnlyMutant.hasCpDialog && pickOnlyMutant.backOk) {
  throw new Error("R14n: head with pick-always-handoff must fail pick-only history pin");
}

const oldLayer = fs.readFileSync(path.join(here, "fixtures/cp-layer-pre-r14d-popstate.mjs"), "utf8");
const hb1BusyPopstateGuardNeedle = "if (active && (active.busy || active._submitInFlight)) {";

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

function assertHb1BusySubmitStillInFlight({ runInApp, document, cap, correctPath }) {
  if (runInApp("window.cpLayer.active == null")) {
    throw new Error("HB1: cp session must remain active during busy submit after stale popstate");
  }
  if (!runInApp("window.cpLayer.active?.busy === true")) {
    throw new Error("HB1: submit must stay busy after stale popstate");
  }
  const root = document.getElementById("cpLayer");
  if (!root || root.classList.contains("hidden")) {
    throw new Error("HB1: layer must stay visible during busy submit after stale popstate");
  }
  const hang = cap.hangingPosts.get(correctPath);
  if (!hang?.pending) {
    throw new Error("HB1: wallet correct POST must remain in-flight (no double submit)");
  }
}

async function runHb1BusyGuardScenario(layerSource, delayMs) {
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
  if (delayMs === 0) {
    dispatchStalePopstate(window);
  } else {
    await new Promise((r) => setTimeout(r, delayMs));
    dispatchStalePopstate(window);
  }
  await new Promise((r) => setTimeout(r, 0));
  assertHb1BusySubmitStillInFlight({ runInApp, document, cap, correctPath });
}

if (!headLayer.includes(hb1BusyPopstateGuardNeedle)) {
  throw new Error("HB1: head cp-layer must guard popstate while submit is busy");
}

const hb1GuardlessLayer = headLayer.replace(
  /if \(active && \(active\.busy \|\| active\._submitInFlight\)\) \{\s*\n[\s\S]*?return;\s*\n\s*\}/,
  "/* HB1 mutant: busy popstate guard removed */",
);

for (const delay of [0, 30, 100]) {
  await runHb1BusyGuardScenario(headLayer, delay);
}

let hb1MutantFailed = false;
for (const delay of [0]) {
  try {
    await runHb1BusyGuardScenario(hb1GuardlessLayer, delay);
  } catch (e) {
    if (e?.message?.startsWith("HB1:")) {
      hb1MutantFailed = true;
      break;
    }
    throw e;
  }
}
if (!hb1MutantFailed) {
  throw new Error("HB1: cp-layer without popstate busy guard must fail busy-submit visibility assertion");
}

async function runCpd4xStackScenario(delayMs, layerSource = headLayer) {
  const { window, document, history, runInApp } = loadOperatorApp("http://localhost/operator/", {
    trackKeydown: false,
    layerSource,
  });
  history.pushState({ page: "start" }, "", "/start/");
  history.pushState({ page: "operator" }, "", "/operator/");
  assertOnOperator(window);
  const submitDone = window.cpForm({
    title: "cpd4x",
    fields: [
      { kind: "money", label: "مبلغ", name: "amountToman", positive: true },
      { kind: "money", label: "هدایا", name: "giftToman", positive: true },
    ],
    onSubmit: () => {},
  });
  await waitCpVisible(document);
  await fillMoneyField(document, "amountToman", "1000");
  await fillMoneyField(document, "giftToman", "100");
  await clickCpSubmit(document);
  await waitFor(() => runInApp("window.cpLayer._submitClosing === true"), "multi-field submit-success pop");
  if (delayMs === 0) {
    dispatchStalePopstate(window);
  } else {
    await new Promise((r) => setTimeout(r, delayMs));
    dispatchStalePopstate(window);
  }
  await submitDone;
  await waitCpHidden(document);
  if (runInApp("window.cpLayer.active != null")) {
    throw new Error("cpd4x: stale cp dialog must not remain after submit-close");
  }
  assertOnOperator(window);
}

for (const delay of [0, 30, 100]) {
  await runCpd4xStackScenario(delay);
}

function isBaseOperatorHistoryState(state) {
  return state === null || state?.page === "operator";
}

async function waitBaseOperatorHistory(history, label) {
  await waitFor(() => isBaseOperatorHistoryState(history.state), label);
}

function assertBaseOperatorHistory(window, history, backs) {
  assertOnOperator(window);
  const stateLabel = history.state === null ? "null" : JSON.stringify(history.state);
  if (backs < 1) {
    throw new Error(
      `after cancel close must history.back (backs=${backs} state=${stateLabel} len=${history.length})`,
    );
  }
  if (!isBaseOperatorHistoryState(history.state)) {
    throw new Error(
      `after close current entry must be the base operator entry (/operator/ state=${stateLabel} len=${history.length}) backs=${backs}`,
    );
  }
}

async function runCpd4xCancelScenario(delayMs, layerSource = headLayer, { waitPopClose = true } = {}) {
  const { window, document, history, runInApp } = loadOperatorApp("http://localhost/operator/", {
    trackKeydown: false,
    layerSource,
  });
  let programmaticBacks = 0;
  const nativeBack = history.back.bind(history);
  history.back = (...args) => {
    programmaticBacks += 1;
    return nativeBack(...args);
  };
  history.pushState({ page: "start" }, "", "/start/");
  history.pushState({ page: "operator" }, "", "/operator/");
  assertOnOperator(window);
  const closed = window.cpForm({
    title: "cpd4x-cancel",
    fields: [
      { kind: "money", label: "مبلغ", name: "amountToman", positive: true },
      { kind: "money", label: "هدایا", name: "giftToman", positive: true },
    ],
    onSubmit: () => {},
  });
  await waitCpVisible(document);
  const cancelBtn = document.querySelector('[data-cp-role="cancel"]');
  if (!cancelBtn) throw new Error("cpd4x-cancel: missing cancel button");
  cancelBtn.click();
  if (waitPopClose) {
    await waitFor(() => runInApp("window.cpLayer._submitClosing === true"), "cancel close history pop");
  } else {
    await waitFor(() => runInApp("window.cpLayer.active == null"), "cancel layer inactive");
  }
  if (delayMs === 0) {
    dispatchStalePopstate(window);
  } else {
    await new Promise((r) => setTimeout(r, delayMs));
    dispatchStalePopstate(window);
  }
  await closed;
  await waitCpHidden(document);
  await waitBaseOperatorHistory(history, "cancel base operator history");
  assertBaseOperatorHistory(window, history, programmaticBacks);
}

for (const delay of [0, 30, 100]) {
  await runCpd4xCancelScenario(delay);
}

const cancelFinish = headLayer.match(
  /if \(reason === "submit-success" \|\| reason === "cancel"\) \{[\s\S]*?\n      \} else if \(reason === "handoff"\)/,
);
if (!cancelFinish?.[0]?.includes("popHistoryIfNeeded")) {
  throw new Error("N2: user cancel must call popHistoryIfNeeded");
}
if (cancelFinish?.[0]?.includes("releaseCpHistoryWithoutBack")) {
  throw new Error("N2: user cancel must not use releaseCpHistoryWithoutBack");
}

const releaseCpHistoryStub = `function releaseCpHistoryWithoutBack() {
  cpLayer._historyPushed = false;
  try { if (history.state && history.state.cpDialog) history.replaceState(null, "", window.location.href); } catch (_) {}
  return Promise.resolve();
}`;

const n2ReplaceCancelPopLayer = `${releaseCpHistoryStub}\n${headLayer.replace(
  /if \(reason === "submit-success" \|\| reason === "cancel"\) \{[\s\S]*?\n      \} else if \(reason === "handoff"\)/,
  `if (reason === "submit-success") {
        cpLayer._submitClosing = true;
        historyPop = popHistoryIfNeeded().finally(() => {
          cpLayer._submitClosing = false;
        });
      } else if (reason === "cancel") {
        historyPop = releaseCpHistoryWithoutBack();
      } else if (reason === "handoff")`,
)}`;

let n2CancelMutantFailed = false;
for (const delay of [0]) {
  try {
    await runCpd4xCancelScenario(delay, n2ReplaceCancelPopLayer, { waitPopClose: false });
  } catch (e) {
  if (e?.message?.includes("base operator entry") || e?.message?.includes("must history.back")) {
      n2CancelMutantFailed = true;
      break;
    }
    throw e;
  }
}
if (!n2CancelMutantFailed) {
  throw new Error("N2: cancel replaceState mutant must fail base-operator history assertion");
}

if (!cancelFinish?.[0]?.includes("popHistoryIfNeeded")) {
  throw new Error("N2: submit-success/cancel must call popHistoryIfNeeded");
}

const n2ReplaceSubmitPopLayer = `${releaseCpHistoryStub}\n${headLayer.replaceAll(
  "historyPop = popHistoryIfNeeded()",
  "historyPop = releaseCpHistoryWithoutBack()",
)}`;
const n2MutantFinish = n2ReplaceSubmitPopLayer.match(
  /if \(reason === "submit-success" \|\| reason === "cancel"\) \{[\s\S]*?\n      \} else if \(reason === "handoff"\)/,
);
if (!n2MutantFinish?.[0]?.includes("releaseCpHistoryWithoutBack")) {
  throw new Error("N2 mutant fixture must swap popHistoryIfNeeded for releaseCpHistoryWithoutBack");
}

const ce15efbLayer = fs.readFileSync(path.join(here, "fixtures/r14m-ce15efb-cp-layer.js"), "utf8");
const mutL1Layer = fs.readFileSync(path.join(here, "fixtures/r14m-mut-L1.js"), "utf8");
const mutL2Layer = fs.readFileSync(path.join(here, "fixtures/r14m-mut-L2.js"), "utf8");

async function dismissCorrectionCancel(document, runInApp, history, len0) {
  const cancelBtn = document.querySelector('[data-cp-role="cancel"]');
  if (!cancelBtn) throw new Error("R14m handoff: missing cancel");
  cancelBtn.click();
  await waitFor(() => runInApp("window.cpLayer.active == null"), "R14m handoff cancel close");
  await waitCpHidden(document);
  await waitFor(
    () => !(history.state && history.state.cpDialog),
    "R14m handoff cancel no cpDialog state",
  );
}

async function runHandoffHistoryPin(layerSource, { expectClean = true, exit = "cancel" } = {}) {
  const { window, document, history, runInApp, ctx } = loadOperatorApp("http://localhost/operator/", {
    trackKeydown: false,
    layerSource,
  });
  history.pushState({ page: "start" }, "", "/start/");
  history.pushState({ page: "operator" }, "", "/operator/");
  assertOnOperator(window);
  const len0 = history.length;
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
  const lenHandoff = history.length;
  const handoffOk = lenHandoff === len0 + 1;
  if (expectClean && !handoffOk) {
    throw new Error(`R14m: after handoff expected history.length ${len0 + 1}, got ${lenHandoff}`);
  }
  if (exit === "cancel") {
    await dismissCorrectionCancel(document, runInApp, history, len0);
  } else {
    throw new Error(`unsupported exit ${exit}`);
  }
  const lenEnd = history.length;
  const stateIsNullOnly = history.state === null;
  const stillCpDialog = !!(history.state && history.state.cpDialog);
  const dismissOk = lenEnd === lenHandoff && !stateIsNullOnly && !stillCpDialog;
  if (expectClean && !dismissOk) {
    throw new Error(
      `R14m: after dismiss expected len ${lenHandoff} and base operator state, got len=${lenEnd} state=${JSON.stringify(history.state)}`,
    );
  }
  if (!expectClean) {
    return { handoffOk, dismissOk };
  }
  assertOnOperator(window);
  return { handoffOk: true, dismissOk: true };
}

await runHandoffHistoryPin(headLayer, { expectClean: true });

for (const [label, layer] of [
  ["ce15efb", ce15efbLayer],
  ["mut-L1", mutL1Layer],
  ["mut-L2", mutL2Layer],
]) {
  const r = await runHandoffHistoryPin(layer, { expectClean: false });
  if (r.handoffOk && r.dismissOk) {
    throw new Error(`R14n: ${label} unexpectedly satisfies clean handoff history pin`);
  }
}

if (!headLayer.includes("_handoffKeepHistory")) {
  throw new Error("R14m: head cp-layer must set _handoffKeepHistory on handoff");
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