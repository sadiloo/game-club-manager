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

let submitted;
const p = window.cpPrompt({
  title: "F07",
  field: { kind: "number", label: "دقیقه", positive: true },
  onSubmit: async (v) => {
    submitted = v;
  },
});
await sleep(30);
const input = document.querySelector('input[data-cp-field="value"]');
input.value = "0";
document.querySelector('[data-cp-role="submit"]').click();
await sleep(30);
if (submitted !== undefined) throw new Error("zero minutes must not submit");
const err = document.querySelector(".cp-field-error:not(.hidden)");
if (!err || err.textContent !== "دقیقه باید بیشتر از صفر باشد.") {
  throw new Error("expected positive-only error for 0 minutes");
}
document.querySelector('[data-cp-role="cancel"]').click();
await p;

console.log("cp-positive-number-selftest: ok");