import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  loadOperatorApp,
  installFetchCapture,
  waitCpVisible,
  clickCpSubmit,
} from "./operator-jsdom-harness.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../src/GameClub.Server/wwwroot/operator");
const headLayer = fs.readFileSync(path.join(root, "cp-layer.js"), "utf8");

const busyNeedle = "if (this.busy && reason !== \"history\") return;";

async function runBusyDismissScenario(layerSource) {
  const { window, document, runInApp, ctx } = loadOperatorApp("http://localhost/operator/", { layerSource });
  const cap = installFetchCapture(window, ctx);
  const done = window.cpForm({
    title: "busy-dismiss",
    fields: [{ kind: "money", label: "مبلغ", name: "amountToman", positive: true }],
    onSubmit: () => new Promise(() => {}),
  });
  await waitCpVisible(document);
  const input = document.querySelector('[data-cp-field="amountToman"]');
  input.value = "1000";
  input.dispatchEvent(new window.Event("input", { bubbles: true }));
  await clickCpSubmit(document);
  await new Promise((r) => setTimeout(r, 0));
  if (!runInApp("window.cpLayer.active?.busy === true")) {
    throw new Error("submit must stay busy");
  }
  document.querySelector(".cp-scrim")?.click();
  document.querySelector(".cp-close")?.click();
  await new Promise((r) => setTimeout(r, 0));
  if (!runInApp("window.cpLayer.active != null")) {
    throw new Error("busy scrim/close must keep cp layer open");
  }
  if (document.getElementById("cpLayer")?.classList.contains("hidden")) {
    throw new Error("busy dismiss must not hide cp layer root");
  }
  void done;
}

if (!headLayer.includes(busyNeedle)) {
  throw new Error("head cp-layer must guard cancel while busy");
}

await runBusyDismissScenario(headLayer);

const guardlessLayer = headLayer.replace(
  busyNeedle,
  "/* R14k mutant: busy cancel guard removed */",
);

let mutantFailed = false;
try {
  await runBusyDismissScenario(guardlessLayer);
} catch (e) {
  if (e?.message?.includes("keep cp layer") || e?.message?.includes("must not hide")) {
    mutantFailed = true;
  } else {
    throw e;
  }
}
if (!mutantFailed) {
  throw new Error("cp-layer without busy cancel guard must fail dismiss-while-busy assertion");
}

console.log("cp-layer-busy-dismiss-guard-selftest: ok");