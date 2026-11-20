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
  title: "F17",
  field: {
    kind: "money",
    label: "تنخواه (تومان)",
    required: false,
    allowZero: true,
    positive: false,
    help: "خالی یعنی ۰ تومان.",
  },
  onSubmit: async (values) => {
    submitted = values?.value ?? 0;
  },
});
await sleep(30);
const input = document.querySelector('input[data-cp-field="value"]');
input.value = "";
document.querySelector('[data-cp-role="submit"]').click();
await sleep(40);
if (submitted !== 0) throw new Error(`empty F17 must submit 0, got ${JSON.stringify(submitted)}`);
document.querySelector('[data-cp-role="close"]')?.click();
await p;

console.log("cp-f17-empty-zero-selftest: ok");