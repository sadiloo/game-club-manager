import fs from "node:fs";
import path from "node:path";
import { JSDOM } from "jsdom";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const layerJs = fs.readFileSync(
  path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/cp-layer.js"),
  "utf8",
);
const appJs = fs.readFileSync(
  path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/app.js"),
  "utf8",
);

const dom = new JSDOM(`<!DOCTYPE html><html><body></body></html>`, { url: "http://localhost/" });
const { window } = dom;
globalThis.window = window;
globalThis.document = window.document;
globalThis.history = window.history;
window.matchMedia = () => ({ matches: true });
window.scrollTo = () => {};

vm.runInThisContext(layerJs);

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

function extractOnSubmit(appSource, anchor) {
  const i = appSource.indexOf(anchor);
  if (i < 0) throw new Error(`anchor not found: ${anchor}`);
  const chunk = appSource.slice(i, i + 4000);
  const m = chunk.match(/onSubmit:\s*(async\s*\(\w+\)\s*=>\s*\{[\s\S]*?\n    \})/);
  if (!m) throw new Error(`onSubmit not found after ${anchor}`);
  return m[1];
}

function onSubmitFromApp(anchor) {
  const src = extractOnSubmit(appJs, anchor);
  return new Function(
    "api",
    "linked",
    "selectedSessionId",
    "refreshPrepaidStatus",
    "refreshShiftState",
    "refreshWalletBalance",
    "cpToast",
    `return ${src}`,
  )(globalThis.api, globalThis.linked, globalThis.selectedSessionId, globalThis.refreshPrepaidStatus, globalThis.refreshShiftState, globalThis.refreshWalletBalance, globalThis.cpToast);
}

const apiBodies = [];
globalThis.api = async (_url, opts) => {
  if (opts?.body) apiBodies.push(JSON.parse(opts.body));
  return {};
};
globalThis.selectedSessionId = "11111111-1111-1111-1111-111111111111";
globalThis.refreshPrepaidStatus = async () => {};
globalThis.refreshShiftState = async () => {};
globalThis.refreshWalletBalance = async () => {};
globalThis.cpToast = () => {};

async function submitPrompt(field, onSubmit, value) {
  apiBodies.length = 0;
  const p = window.cpPrompt({
    title: "t",
    confirmLabel: "ok",
    field,
    onSubmit,
  });
  await sleep(25);
  const input = document.querySelector('input[data-cp-field="value"]');
  input.value = value;
  document.querySelector('[data-cp-role="submit"]').click();
  await sleep(50);
  await p;
  document.querySelector('[data-cp-role="close"]')?.click();
  await sleep(10);
}

function assertNumberField(body, key, expected) {
  if (typeof body[key] !== "number") throw new Error(`${key} must be number, got ${JSON.stringify(body[key])}`);
  if (body[key] !== expected) throw new Error(`${key} expected ${expected}, got ${body[key]}`);
}

async function main() {
  const f07OnSubmit = onSubmitFromApp('bindClick("prepaidDurationBtn"');
  await submitPrompt(
    {
      kind: "number",
      label: "دقیقه",
      positive: true,
      emptyLabel: "x",
      positiveErr: "y",
      formatLabel: "z",
    },
    f07OnSubmit,
    "60",
  );
  if (apiBodies.length !== 1) throw new Error("F07 must call api once");
  assertNumberField(apiBodies[0], "minutes", 60);

  const f08OnSubmit = onSubmitFromApp('bindClick("prepaidAmountBtn"');
  await submitPrompt({ kind: "money", label: "m", positive: true }, f08OnSubmit, "55000");
  assertNumberField(apiBodies[0], "amountToman", 55000);

  globalThis.linked = { id: 42, name: "t" };
  const f09OnSubmit = onSubmitFromApp('bindClick("mgrGiftBtn"');
  await submitPrompt({ kind: "money", label: "h", positive: true }, f09OnSubmit, "50000");
  assertNumberField(apiBodies[0], "giftToman", 50000);

  apiBodies.length = 0;
  let giftGuardThrew = false;
  try {
    await f09OnSubmit(0);
  } catch {
    giftGuardThrew = true;
  }
  if (!giftGuardThrew) throw new Error("F09 handler must reject 0");
  if (apiBodies.length !== 0) throw new Error("F09 zero must not call api");

  const f17OnSubmit = onSubmitFromApp('bindClick("openShiftBtn"');
  await submitPrompt(
    { kind: "money", label: "تنخواه", required: false, allowZero: true, positive: false },
    f17OnSubmit,
    "50000",
  );
  assertNumberField(apiBodies[0], "openingFloatToman", 50000);

  const pRet = window.cpPrompt({
    title: "F01",
    field: { kind: "money", label: "m", positive: true },
  });
  await sleep(20);
  document.querySelector('input[data-cp-field="value"]').value = "20000";
  document.querySelector('[data-cp-role="submit"]').click();
  const ret = await pRet;
  if (typeof ret !== "number" || ret !== 20000) throw new Error(`F01 return must be number 20000, got ${JSON.stringify(ret)}`);

  const pPay = window.cpPrompt({
    title: "pay",
    field: { kind: "money", label: "m", positive: true },
    validate: null,
  });
  await sleep(20);
  document.querySelector('input[data-cp-field="value"]').value = "12345";
  document.querySelector('[data-cp-role="submit"]').click();
  const payAmt = await pPay;
  if (payAmt !== 12345) throw new Error(`F05 return amount expected 12345, got ${payAmt}`);

  console.log("cp-prompt-submit-bodies-selftest: ok");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});