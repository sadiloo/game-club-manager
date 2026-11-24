import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";

const here = path.dirname(fileURLToPath(import.meta.url));
const appJs = path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/app.js");
const js = fs.readFileSync(appJs, "utf8");
const parserStart = js.indexOf("function ss14IsRateDigitChar");
const parserEnd = js.indexOf("function ss14OptionLabel", parserStart);
const saveStart = js.indexOf("async function pcSavePcRates");
const saveEnd = js.indexOf("\n\n", saveStart + 1) > 0 ? js.indexOf("document.addEventListener", saveStart) : saveStart + 1500;
if (parserStart < 0 || parserEnd < 0 || saveStart < 0) {
  console.error("Could not locate shipped pc rates blocks in app.js");
  process.exit(2);
}

const dom = new JSDOM(`<!DOCTYPE html><html><body></body></html>`);
const window = dom.window;
const document = window.document;
window.console = console;
window.api = async () => {
  throw new Error("api should not be called when validation fails");
};
vm.createContext(window);
vm.runInContext(js.slice(parserStart, parserEnd), window);
vm.runInContext(js.slice(saveStart, saveEnd), window);

const { ss14ParseTomanInput, pcSavePcRates } = window;

const wrap = document.createElement("div");
wrap.innerHTML = `
  <input data-f="onlineDay" value="80000"/>
  <input data-f="onlineNight" value="100000"/>
  <input data-f="offlineDay" value="abc"/>
  <input data-f="offlineNight" value=""/>
`;
const err = document.createElement("div");
await pcSavePcRates("pc-normal", wrap, err);
if (err.textContent !== "عدد معتبر وارد کنید.") {
  throw new Error(`expected invalid offline parse message, got: ${err.textContent}`);
}

wrap.innerHTML = `
  <input data-f="onlineDay" value="80000"/>
  <input data-f="onlineNight" value="100000"/>
  <input data-f="offlineDay" value="50000"/>
  <input data-f="offlineNight" value=""/>
`;
err.textContent = "";
await pcSavePcRates("pc-normal", wrap, err);
if (err.textContent !== "نرخ آفلاین روز و شب را با هم وارد کنید.") {
  throw new Error(`expected offline pair message, got: ${err.textContent}`);
}

if (ss14ParseTomanInput("۸۰۰۰۰") !== 80_000) {
  throw new Error("Persian digit parse failed for online day");
}

if (ss14ParseTomanInput("١٠٠٠٠٠") !== 100_000) {
  throw new Error("Arabic digit parse failed for online night");
}

console.log("pc-rates-shipped-parser-selftest: ok");