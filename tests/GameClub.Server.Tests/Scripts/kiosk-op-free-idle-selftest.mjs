import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";

const here = path.dirname(fileURLToPath(import.meta.url));
const appJs = path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/app.js");
const js = fs.readFileSync(appJs, "utf8");
const start = js.indexOf("// --- K-OP (MVP installer B) ---");
const saveEnd = js.indexOf("async function kioskOpSaveFreeIdleMinutes", start);
if (start < 0 || saveEnd < 0) {
  console.error("Could not locate K-OP free-idle helpers in app.js");
  process.exit(2);
}

const dom = new JSDOM(
  `<!DOCTYPE html><html dir="rtl"><body>
    <input id="mgrFreeIdleMinutes" value="۱۰" />
    <p id="mgrFreeIdleErr" class="hidden"></p>
  </body></html>`,
  { url: "http://panel.test/" }
);
const { window } = dom;

vm.createContext(window);
vm.runInContext(js.slice(start, saveEnd), window);

const forbidden = window.kioskOpFreeIdleErrorMessage("free_idle_forbidden", "mgr.freeIdle.err.forbidden");
if (forbidden !== "فقط مدیر یا مالک می‌تواند این را عوض کند.") {
  console.error("unexpected forbidden text", forbidden);
  process.exit(1);
}
const invalid = window.kioskOpFreeIdleErrorMessage("free_idle_invalid", "mgr.freeIdle.err.invalid");
if (invalid !== "یک عدد کامل از ۱ تا ۲۴۰ وارد کنید.") {
  console.error("unexpected invalid text", invalid);
  process.exit(1);
}
window.kioskOpShowMgrFreeIdleErr(forbidden);
const shown = window.document.getElementById("mgrFreeIdleErr").textContent;
if (shown.includes("mgr.") || shown !== forbidden) {
  console.error("inline error must be Persian with no mgr. key", shown);
  process.exit(1);
}

console.log("kiosk-op-free-idle-selftest: ok");