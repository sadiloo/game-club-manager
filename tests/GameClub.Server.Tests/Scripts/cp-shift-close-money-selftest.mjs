import { loadOperatorApp } from "./operator-jsdom-harness.mjs";

const { window } = loadOperatorApp();

const parseCloseToman = (raw) => {
  const trimmed = String(raw ?? "").trim();
  if (!trimmed) return null;
  return window.cpParseNumber(trimmed, { allowZero: true, positive: false });
};

if (parseCloseToman("") !== null) throw new Error("empty close cash must reject");
if (parseCloseToman("0") !== 0) throw new Error("zero close cash allowed");
if (parseCloseToman("۱۲.۵") !== null) throw new Error("12.5 must reject");
if (parseCloseToman("50,000") !== 50000) throw new Error("grouped cash");
if (parseCloseToman("1e5") !== null) throw new Error("scientific notation must reject");

console.log("cp-shift-close-money-selftest: ok");