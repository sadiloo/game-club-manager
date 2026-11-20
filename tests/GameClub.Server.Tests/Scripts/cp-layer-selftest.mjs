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

const nativeFail = () => {
  throw new Error("native dialog forbidden");
};
window.prompt = nativeFail;
window.alert = nativeFail;
window.confirm = nativeFail;

vm.runInThisContext(layerJs);
globalThis.cpPrompt = window.cpPrompt;
globalThis.cpLayer = window.cpLayer;

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

const p = cpPrompt({
  title: "تست",
  confirmLabel: "باشه",
  field: { kind: "money", label: "مبلغ", positive: true },
});
await sleep(30);
if (!cpLayer.active) throw new Error("layer should be active");
document.querySelector('[data-cp-role="cancel"]').click();
await sleep(20);
if (cpLayer.active) throw new Error("layer should close on cancel");
const result = await p;
if (result !== null) throw new Error("cancel should return null");

console.log("cp-layer-selftest: ok");