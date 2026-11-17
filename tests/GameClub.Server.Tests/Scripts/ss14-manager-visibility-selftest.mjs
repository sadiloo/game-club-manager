import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";

const here = path.dirname(fileURLToPath(import.meta.url));
const appJs = path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/app.js");
const js = fs.readFileSync(appJs, "utf8");
const start = js.indexOf("function ss14IsRateDigitChar");
const end = js.indexOf("function ss14OptionLabel", start);
if (start < 0 || end < 0) {
  console.error("Could not locate shipped SS-14 rate parser block in app.js");
  process.exit(2);
}

const dom = new JSDOM(
  `<!DOCTYPE html><html><head></head><body style="width:390px;margin:0;display:block"><div id="cafeSettings"></div></body></html>`,
  { pretendToBeVisual: true, url: "http://panel.test/", resources: "usable" },
);
const { window } = dom;
const doc = window.document;

function isZeroCssWidth(w) {
  if (!w || w === "auto" || w.endsWith("%")) return false;
  const n = parseFloat(w);
  return Number.isFinite(n) && n <= 0;
}

const panelStart = js.indexOf("function ss14FmtToman");
const panelEnd = js.indexOf("// --- end SS-14 ---", panelStart);
if (panelStart < 0 || panelEnd < 0) {
  console.error("Could not locate SS-14 manager panel block in app.js");
  process.exit(2);
}

window.console = console;
window.api = async () => [];
vm.createContext(window);
vm.runInContext(
  "var ss14MgrTitles = [];\nvar ss14MgrConsoleKeys = [];\nvar ss14MgrSelectedId = null;\n" +
    js.slice(panelStart, panelEnd + "// --- end SS-14 ---".length),
  window,
);

window.ss14MgrConsoleKeys = [
  { mode: "Offline", playerCount: 1 },
  { mode: "Offline", playerCount: 2 },
];
window.ss14MgrTitles = [{ id: 1, name: "T", enabled: true, rates: [] }];
window.ss14MgrSelectedId = 1;
window.ss14EnsureManagerPanel();
window.ss14RenderManagerForm(window.ss14MgrTitles[0]);

const labels = [...doc.querySelectorAll("#ss14GameRatesBody .ss14-rate-cell label")];
if (labels.length < 4) {
  throw new Error(`expected at least 4 rate labels, got ${labels.length}`);
}
for (const el of labels) {
  const s = window.getComputedStyle(el);
  const cell = el.closest(".ss14-rate-cell");
  const cs = cell ? window.getComputedStyle(cell) : null;
  const w = s.width;
  const cw = cs?.width ?? "";
  if (s.display === "none" || s.visibility === "hidden" || isZeroCssWidth(w)) {
    throw new Error(`label not visible: display=${s.display} visibility=${s.visibility} width=${w} text=${el.textContent}`);
  }
  if (isZeroCssWidth(cw)) {
    throw new Error(`rate cell has zero computed width: ${cw}`);
  }
  if (!(el.textContent || "").includes("تومان در ساعت")) {
    throw new Error(`unexpected label text: ${el.textContent}`);
  }
}

const name = doc.getElementById("ss14GameName");
const hourly = doc.querySelector('#ss14GameRatesBody input[data-rate="hourly"]');
name.value = "Bad";
hourly.value = "000,150";
await window.ss14SaveManagerTitle();

const err = doc.getElementById("ss14GameRatesError");
if (!err || !(err.textContent || "").includes("نامعتبر")) {
  throw new Error(`expected Persian inline error, got ${JSON.stringify(err?.textContent)}`);
}
const es = window.getComputedStyle(err);
if (es.display === "none" || es.visibility === "hidden" || isZeroCssWidth(es.width)) {
  throw new Error(`inline error not visible: display=${es.display} visibility=${es.visibility} width=${es.width}`);
}

console.log("ss14-manager-visibility-selftest: ok");