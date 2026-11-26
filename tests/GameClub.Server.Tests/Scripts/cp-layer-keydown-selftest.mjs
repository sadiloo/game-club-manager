import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { JSDOM } from "jsdom";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const layerJs = fs.readFileSync(
  path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/cp-layer.js"),
  "utf8",
);

const dom = new JSDOM(`<!DOCTYPE html><html><body></body></html>`, { url: "http://localhost/operator/" });
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

let keydownListeners = 0;
const origAdd = window.Element.prototype.addEventListener;
const origRemove = window.Element.prototype.removeEventListener;
window.Element.prototype.addEventListener = function (type, fn, opts) {
  if (type === "keydown" && this.id === "cpLayer") keydownListeners++;
  return origAdd.call(this, type, fn, opts);
};
window.Element.prototype.removeEventListener = function (type, fn, opts) {
  if (type === "keydown" && this.id === "cpLayer") keydownListeners--;
  return origRemove.call(this, type, fn, opts);
};

async function openClosePrompt(value) {
  const p = window.cpPrompt({
    title: "t",
    confirmLabel: "ok",
    field: { kind: "money", label: "m", positive: true },
  });
  await sleep(15);
  document.querySelector('input[data-cp-field="value"]').value = value;
  document.querySelector('[data-cp-role="close"]').click();
  await sleep(15);
  await p;
  if (keydownListeners !== 0) throw new Error(`after close expected 0 keydown listeners, got ${keydownListeners}`);
}

for (let i = 0; i < 3; i++) await openClosePrompt(String(1000 + i));

const p = window.cpPrompt({
  title: "final",
  confirmLabel: "ok",
  field: { kind: "money", label: "m", positive: true },
});
await sleep(15);
if (keydownListeners !== 1) throw new Error(`expected 1 keydown listener on open, got ${keydownListeners}`);
document.querySelector('[data-cp-role="close"]').click();
await sleep(15);
await p;
if (keydownListeners !== 0) throw new Error(`after final close expected 0 listeners, got ${keydownListeners}`);

let posts = 0;
const p2 = window.cpPrompt({
  title: "post",
  confirmLabel: "ok",
  field: { kind: "money", label: "m", positive: true },
  onSubmit: async () => {
    posts++;
  },
});
await sleep(15);
document.querySelector('input[data-cp-field="value"]').value = "50000";
const submit = document.querySelector('[data-cp-role="submit"]');
submit.click();
submit.click();
await sleep(50);
if (!document.getElementById("cpLayer").classList.contains("hidden")) {
  throw new Error("form should close after submit");
}
await p2;
if (posts !== 1) throw new Error(`expected 1 POST after guarded double submit, got ${posts}`);

console.log("cp-layer-keydown-selftest: ok");