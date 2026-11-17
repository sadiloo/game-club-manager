import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const appJs = path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator/app.js");
const js = fs.readFileSync(appJs, "utf8");
const start = js.indexOf("function ss14IsRateDigitChar");
const end = js.indexOf("function ss14OptionLabel", start);
if (start < 0 || end < 0) {
  console.error("Could not locate shipped SS-14 rate parser functions in app.js");
  process.exit(2);
}

const ctx = { console };
vm.createContext(ctx);
vm.runInContext(js.slice(start, end), ctx);

const { ss14IsAllowedRateInput, ss14ParseTomanInput } = ctx;

function expectNull(input) {
  if (ss14ParseTomanInput(input) !== null || ss14IsAllowedRateInput(input) !== false) {
    throw new Error(`expected reject: ${JSON.stringify(input)}`);
  }
}

function expectValue(input, expected) {
  const n = ss14ParseTomanInput(input);
  if (n !== expected) {
    throw new Error(`expected ${expected} for ${JSON.stringify(input)}, got ${n}`);
  }
  if (!ss14IsAllowedRateInput(input)) {
    throw new Error(`expected allowed: ${JSON.stringify(input)}`);
  }
}

expectNull("150,00");
expectNull("1,,5");
expectNull(",150000");
expectNull("150000,");
expectNull("1,50,000");
expectNull("000,150");
expectNull("0,150,000");
expectNull("0150000");
expectNull("15،00");
expectNull("1،50،000");
expectNull("١٥٠٬٠٠");
expectNull("۱۵۰٬۰۰");
expectNull("1٬50٬000");
expectNull("١٬٥٠٬٠٠٠");

expectValue("150000", 150000);
expectValue("150,000", 150000);
expectValue("۱۵۰٬۰۰۰", 150000);
expectValue("١٥٠٬٠٠٠", 150000);
expectValue("150٬000", 150000);
expectValue("1,500,000", 1500000);
expectValue("١٬٥٠٠٬٠٠٠", 1500000);
expectValue("1،500،000", 1500000);

console.log("ss14-shipped-rate-parser-selftest: ok");