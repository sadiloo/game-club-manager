import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const appJs = fs.readFileSync(
  path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../src/GameClub.Server/wwwroot/operator/app.js"),
  "utf8",
);
const ctx = { window: {}, document: {} };
vm.createContext(ctx);
const layerJs = fs.readFileSync(
  path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../src/GameClub.Server/wwwroot/operator/cp-layer.js"),
  "utf8",
);
vm.runInContext(layerJs, ctx);
vm.runInContext("globalThis.cpParseInteger = window.cpParseInteger;", ctx);
vm.runInContext(
  appJs.match(/function cpIsJalaliLeapYear[\s\S]*?^}/m)[0] +
    "\n" +
    appJs.match(/function cpJalaliMonthLength[\s\S]*?^}/m)[0] +
    "\n" +
    appJs.match(/function validateJalaliBirth[\s\S]*?^}/m)[0] +
    "\n" +
    appJs.match(/function cpCurrentJalaliYear[\s\S]*?^}/m)[0],
  ctx,
);

if (!ctx.cpIsJalaliLeapYear(1403)) throw new Error("1403 must be leap");
if (ctx.cpIsJalaliLeapYear(1402)) throw new Error("1402 must not be leap");
if (ctx.validateJalaliBirth(1403, 12, 30) !== null) throw new Error("1403-12-30 must be valid");
if (ctx.validateJalaliBirth(1402, 12, 30) === null) throw new Error("1402-12-30 must be invalid");

console.log("cp-jalali-leap-selftest: ok");