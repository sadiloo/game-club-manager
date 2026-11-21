import { loadOperatorApp } from "./operator-jsdom-harness.mjs";

const { window } = loadOperatorApp();

let blocked = false;
try {
  window.alert("blocked");
} catch (err) {
  blocked = String(err?.message || err).includes("native dialog forbidden");
}
if (!blocked) throw new Error("operator app must block native alert");

console.log("cp-native-dialog-guard-selftest: ok");