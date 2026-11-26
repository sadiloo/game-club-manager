import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { loadOperatorApp } from "./operator-jsdom-harness.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const indexPath = path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/index.html");
const html = fs.readFileSync(indexPath, "utf8");

if (/body\s*\{[^}]*overflow-x:\s*hidden/i.test(html)) {
  throw new Error("body must not use overflow-x:hidden (hides real overflow bugs)");
}
for (const needle of [
  ".sale-line > *",
  ".pay-line > *",
  ".cp-body, .cp-warning",
  "min-width: 0",
  "#sessionLabel",
]) {
  if (!html.includes(needle)) throw new Error(`index.html missing narrow-viewport rule: ${needle}`);
}

const long = "X".repeat(85);
const { document } = loadOperatorApp();
document.getElementById("customerChip").classList.remove("hidden");
document.getElementById("customerChip").textContent = long;
const docSw = document.documentElement.scrollWidth;
if (docSw !== 0) {
  throw new Error(`jsdom reported scrollWidth=${docSw}; expected 0 (no layout engine)`);
}

console.log("cp-operator-narrow-viewport-selftest: jsdom has no layout; Chromium asserts scrollWidth in InpageFormsOperatorNarrowViewportLayoutTests");
console.log("cp-operator-narrow-viewport-selftest: ok");