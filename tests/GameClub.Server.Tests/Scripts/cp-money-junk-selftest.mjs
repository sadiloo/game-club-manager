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

vm.runInThisContext(layerJs);

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

const p = window.cpPrompt({
  title: "money junk",
  field: { kind: "money", label: "مبلغ", positive: true },
});
await sleep(30);
const input = document.querySelector('input[data-cp-field="value"]');
input.value = "12.5";
document.querySelector('[data-cp-role="submit"]').click();
await sleep(30);
const err = document.querySelector(".cp-field-error:not(.hidden)");
if (!err || err.textContent !== "مبلغ درست نیست؛ فقط رقم بنویسید.") {
  throw new Error(`junk money should show invalid_format, got: ${err?.textContent}`);
}
document.querySelector('[data-cp-role="cancel"]').click();
await p;

console.log("cp-money-junk-selftest: ok");