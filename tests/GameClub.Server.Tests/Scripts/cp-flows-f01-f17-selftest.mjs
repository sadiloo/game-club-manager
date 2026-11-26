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

const dom = new JSDOM(`<!DOCTYPE html><html><body></body></html>`, { url: "http://localhost/" });
const { window } = dom;
globalThis.window = window;
globalThis.document = window.document;
globalThis.history = window.history;
window.matchMedia = () => ({ matches: true });
window.scrollTo = () => {};
globalThis.fetch = async () => ({ ok: true, status: 200, text: async () => "{}" });

const nativeFail = () => {
  throw new Error("native dialog forbidden");
};
window.prompt = nativeFail;
window.alert = nativeFail;
window.confirm = nativeFail;

vm.runInThisContext(layerJs);
globalThis.cpPrompt = window.cpPrompt;
globalThis.cpParseNumber = window.cpParseNumber;

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

// F17 cancel → no fetch
let fetchCount = 0;
globalThis.fetch = async () => {
  fetchCount++;
  return { ok: true, status: 200, text: async () => "{}" };
};

const openP = cpPrompt({
  title: "باز کردن شیفت",
  field: { kind: "money", label: "تنخواه", allowZero: true, value: 0 },
  onSubmit: async () => {
    fetchCount++;
  },
});
await sleep(20);
document.querySelector('[data-cp-role="cancel"]').click();
await openP;
if (fetchCount !== 0) throw new Error(`cancel should not fetch, got ${fetchCount}`);

// F5 reject non-positive client-side without fetch (simulated addPaymentLine validate)
const bad = cpParseNumber("0", { positive: true });
if (bad !== null) throw new Error("zero should fail positive parse");

console.log("cp-flows-f01-f17-selftest: ok");