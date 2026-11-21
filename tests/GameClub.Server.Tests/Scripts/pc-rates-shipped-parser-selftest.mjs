import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const appJs = path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/app.js");
const js = fs.readFileSync(appJs, "utf8");
const parserStart = js.indexOf("function ss14IsRateDigitChar");
const parserEnd = js.indexOf("function ss14OptionLabel", parserStart);
if (parserStart < 0 || parserEnd < 0) {
  console.error("Could not locate ss14ParseTomanInput in app.js");
  process.exit(2);
}

const ctx = { console };
vm.createContext(ctx);
vm.runInContext(js.slice(parserStart, parserEnd), ctx);
const { ss14ParseTomanInput } = ctx;

const saveStart = js.indexOf("async function pcSavePcRates");
const saveSlice = js.slice(saveStart, saveStart + 1200);
if (!saveSlice.includes("(body.offlineDay == null) !== (body.offlineNight == null)")) {
  throw new Error("pcSavePcRates offline-pair guard precedence fix missing");
}

function offlinePairInvalid(offlineDay, offlineNight) {
  return (offlineDay == null) !== (offlineNight == null);
}

if (!offlinePairInvalid(50_000, null)) {
  throw new Error("half offline pair should be invalid");
}

if (offlinePairInvalid(null, null) || offlinePairInvalid(50_000, 60_000)) {
  throw new Error("full or empty offline pair should be valid guard");
}

if (ss14ParseTomanInput("۸۰۰۰۰") !== 80_000) {
  throw new Error("Persian digit parse failed for online day");
}

if (ss14ParseTomanInput("١٠٠٠٠٠") !== 100_000) {
  throw new Error("Arabic digit parse failed for online night");
}

console.log("pc-rates-shipped-parser-selftest: ok");