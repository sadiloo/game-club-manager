import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const layerJs = fs.readFileSync(
  path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../src/GameClub.Server/wwwroot/operator/cp-layer.js"),
  "utf8",
);
const ctx = {
  console,
  window: { addEventListener() {}, matchMedia: () => ({ matches: true }), scrollTo: () => {} },
  document: { getElementById: () => null, body: {} },
};
vm.createContext(ctx);
vm.runInContext(layerJs, ctx);

const cpParseNumberResult = ctx.window.cpParseNumberResult;

function reason(raw, opts = {}) {
  return cpParseNumberResult(raw, opts).reason;
}

if (reason("") !== "empty") throw new Error("empty reason");
if (reason("12.5") !== "format") throw new Error("format reason");
if (reason("0500", { allowZero: true }) !== "leading_zero") throw new Error("leading_zero reason");
if (reason("9999999999999999", { allowZero: true }) !== "too_long") throw new Error("too_long reason");
if (reason("00000000000000000", { allowZero: true }) !== "too_long") throw new Error("too_long before leading_zero");
if (reason("12 000", { allowZero: true }) !== "format") throw new Error("inner space is format");
if (reason("0", { positive: true }) !== "non_positive") throw new Error("non_positive for positive-only zero");

const integerResult = ctx.window.cpParseIntegerResult;
if (integerResult("05").ok !== true || integerResult("05").value !== 5) throw new Error("integer leading zero ok");
if (integerResult("1234567890123456").reason !== "too_long") throw new Error("integer too_long");

console.log("cp-parse-money-reasons-selftest: ok");