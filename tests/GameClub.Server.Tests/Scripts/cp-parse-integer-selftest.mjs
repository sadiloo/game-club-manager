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

const cpParseInteger = ctx.window.cpParseInteger;
const cpParseNumber = ctx.window.cpParseNumber;

function assertNull(v, msg) {
  if (v !== null) throw new Error(`${msg}: expected null, got ${v}`);
}

assertNull(cpParseInteger("۱.۵"), "persian decimal dot");
assertNull(cpParseInteger("1.5"), "latin decimal");
assertNull(cpParseInteger("٫۵"), "arabic decimal");
assertNull(cpParseInteger("-۵"), "minus persian");
assertNull(cpParseInteger("−5"), "unicode minus");
assertNull(cpParseInteger("+10"), "plus sign");
assertNull(cpParseInteger("12 000"), "inner space");
assertNull(cpParseInteger(""), "empty");

if (cpParseInteger("۱۵") !== 15) throw new Error("persian digits");
if (cpParseInteger("٩") !== 9) throw new Error("arabic-indic digits");
if (cpParseInteger("42") !== 42) throw new Error("latin digits");
if (cpParseInteger("05") !== 5) throw new Error("F13 leading zero month/day");
if (cpParseInteger("07") !== 7) throw new Error("F13 leading zero day");
if (ctx.window.cpParseIntegerResult("1234567890123456").reason !== "too_long") throw new Error("integer 16 digits");
if (ctx.window.cpParseIntegerResult("12 000").reason !== "format") throw new Error("integer inner space");

assertNull(cpParseNumber("۱.۵"), "money decimal");
assertNull(cpParseNumber("05", { allowZero: true }), "money leading zero still rejected");
assertNull(cpParseNumber("-1"), "money negative");
if (cpParseNumber("1,500") !== 1500) throw new Error("money grouping");
if (cpParseNumber("150000") !== 150000) throw new Error("money plain");

console.log("cp-parse-integer-selftest: ok");