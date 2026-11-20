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

const xssTitle = 'FIFA <b id="inj">25</b><img src=x onerror="window.__xss=1"><script>window.__xss=2</script>';
const pickP = window.cpPick({
  title: "pick",
  items: [{ id: "1", title: xssTitle, subtitle: "<i>x</i>" }],
});
await sleep(40);
const row = document.querySelector(".cp-pick-row");
if (!row) throw new Error("pick row missing");
if (row.querySelector("b, img, script, i")) throw new Error("HTML elements must not be created from catalog title");
if (row.textContent !== xssTitle + "<i>x</i>") throw new Error(`expected plain text title, got: ${row.textContent}`);
if (window.__xss) throw new Error("script must not run");
document.querySelector(".cp-pick-row")?.click();
await pickP;

console.log("cp-pick-xss-selftest: ok");