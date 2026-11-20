import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const layerJs = path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/cp-layer.js");
const appJs = path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/app.js");

const layer = fs.readFileSync(layerJs, "utf8");
const appStart = fs.readFileSync(appJs, "utf8").indexOf("function ss14IsRateDigitChar");
const appChunk = fs.readFileSync(appJs, "utf8").slice(appStart, fs.readFileSync(appJs, "utf8").indexOf("function ss14OptionLabel", appStart));

const ctx = {
  console,
  window: { addEventListener() {}, matchMedia: () => ({ matches: true }) },
  document: { getElementById: () => null, body: {} },
};
vm.createContext(ctx);
vm.runInContext(layer, ctx);
vm.runInContext(appChunk, ctx);

const cpParseNumber = ctx.window.cpParseNumber;
const ss14ParseTomanInput = ctx.ss14ParseTomanInput;

function assertEq(a, b, msg) {
  if (a !== b) throw new Error(`${msg}: expected ${b}, got ${a}`);
}

assertEq(cpParseNumber("150000"), 150000, "latin");
assertEq(cpParseNumber("0", { allowZero: true }), 0, "allowZero");
assertEq(cpParseNumber("0", { allowZero: false }), null, "reject zero");
assertEq(cpParseNumber("۱۵۰٬۰۰۰"), 150000, "persian");
assertEq(ss14ParseTomanInput("1,500,000"), 1500000, "ss14");

console.log("cp-parse-money-selftest: ok");