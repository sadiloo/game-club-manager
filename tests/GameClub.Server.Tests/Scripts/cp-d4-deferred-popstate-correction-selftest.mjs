import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";
import {
  loadOperatorApp,
  sleep,
  waitCpVisible,
  waitCpHidden,
  fillMoneyField,
  clickCpSubmit,
} from "./operator-jsdom-harness.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, "../../..");
const cpLayerPath = path.join(repo, "src/GameClub.Server/wwwroot/operator/cp-layer.js");

function assertOnOperator(window) {
  if (!String(window.location.href).includes("/operator/")) {
    throw new Error(`left operator: ${window.location.href}`);
  }
}

async function runPromptSubmitWithDeferredBack(layerSource) {
  const { window, document, history } = loadOperatorApp("http://localhost/operator/", {
    trackKeydown: false,
    layerSource,
  });

  history.pushState({ page: "start" }, "", "/start/");
  history.pushState({ page: "operator" }, "", "/operator/");
  assertOnOperator(window);

  let deferredBacks = 0;
  let pendingBack = null;
  const nativeBack = history.back.bind(history);
  history.back = () => {
    deferredBacks += 1;
    pendingBack = () => {
      pendingBack = null;
      nativeBack();
    };
  };

  void window.cpPrompt({ title: "x", field: { kind: "money", label: "m", positive: true } });
  await waitCpVisible(document);
  await fillMoneyField(document, "value", "1000");
  deferredBacks = 0;

  await clickCpSubmit(document);
  await sleep(80);
  if (!pendingBack) throw new Error("submit must schedule a deferred history.back");
  pendingBack();
  await sleep(80);

  await waitCpHidden(document);
  assertOnOperator(window);
  if (deferredBacks !== 1) {
    throw new Error(`expected exactly one programmatic history.back (got ${deferredBacks})`);
  }
}

const headLayer = fs.readFileSync(cpLayerPath, "utf8");
const oldLayer = execFileSync("git", ["-C", repo, "show", "94ef4ff:src/GameClub.Server/wwwroot/operator/cp-layer.js"], {
  encoding: "utf8",
});

const headDefersBack = /setTimeout\(\(\)\s*=>\s*\{[\s\S]*history\.back\(\)/.test(headLayer);
const oldDefersBack = /setTimeout\(\(\)\s*=>\s*\{[\s\S]*history\.back\(\)/.test(oldLayer);
const oldHasWaiters = oldLayer.includes("_historyPopWaiters");

if (!headDefersBack) {
  throw new Error("head cp-layer must defer history.back after submit close");
}

await runPromptSubmitWithDeferredBack();

if (oldDefersBack && oldHasWaiters) {
  throw new Error("94ef4ff cp-layer unexpectedly includes N2 defer/waiters; update this selftest");
}

if (!oldHasWaiters || oldDefersBack) {
  const reason = !oldHasWaiters
    ? "stale popstate: 94ef4ff cp-layer lacks _historyPopWaiters guard"
    : "stale popstate: 94ef4ff cp-layer calls history.back synchronously on close";
  console.log(`cp-d4-deferred-popstate-correction-selftest: ok (head pass; ${reason})`);
} else {
  throw new Error("expected 94ef4ff cp-layer to lack N2 fix");
}