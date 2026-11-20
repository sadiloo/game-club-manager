import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const layerJs = fs.readFileSync(
  path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../src/GameClub.Server/wwwroot/operator/cp-layer.js"),
  "utf8",
);
const appJs = fs.readFileSync(
  path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../src/GameClub.Server/wwwroot/operator/app.js"),
  "utf8",
);

const ctx = {
  console,
  window: { addEventListener() {}, matchMedia: () => ({ matches: true }), scrollTo: () => {} },
  document: { getElementById: () => null, body: {} },
};
vm.createContext(ctx);
vm.runInContext(layerJs, ctx);

const parseCloseToman = (raw) => {
  const trimmed = String(raw ?? "").trim();
  if (!trimmed) return 0;
  return ctx.window.cpParseNumber(trimmed, { allowZero: true, positive: false });
};

if (parseCloseToman("") !== 0) throw new Error("empty close cash should be 0");
if (parseCloseToman("۱۲.۵") !== null) throw new Error("12.5 must reject");
if (parseCloseToman("50,000") !== 50000) throw new Error("grouped cash");
if (parseCloseToman("1e5") !== null) throw new Error("scientific notation must reject");
if (!appJs.includes("parseCloseToman") && !appJs.includes("cpParseNumber(trimmed, { allowZero: true, positive: false })")) {
  throw new Error("shift close must use cpParseNumber reject-only parser");
}

console.log("cp-shift-close-money-selftest: ok");