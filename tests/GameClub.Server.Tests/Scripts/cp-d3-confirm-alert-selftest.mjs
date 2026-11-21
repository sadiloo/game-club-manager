import { loadOperatorApp, installFetchCapture, sleep, waitCpVisible, waitCpHidden } from "./operator-jsdom-harness.mjs";

const SESSION = "22222222-2222-4222-8222-222222222222";

const { window, document, runInApp, ctx } = loadOperatorApp();
const cap = installFetchCapture(window, ctx);
cap.stub(`/api/operator/sessions/${SESSION}/force-logout`, "POST", {});
cap.stub("/api/operator/grid", "GET", [{ id: "ps5-1", sessionId: SESSION, status: "Playing" }]);

runInApp(`selectedSessionId = "${SESSION}"; lastGridTiles = [{ sessionId: "${SESSION}", id: "ps5-1" }];`);

document.getElementById("forceLogoutBtn")?.click();
await waitCpVisible(document);
const confirmBtn = [...document.querySelectorAll(".cp-foot .cp-btn")].find((b) => b.textContent.includes("خروج اجباری"));
if (!confirmBtn) throw new Error("force logout confirm missing");
const active = document.activeElement;
if (!document.getElementById("cpLayer")?.contains(active)) throw new Error("focus must be inside confirm dialog");

cap.reset();
confirmBtn.click();
await sleep(150);
if (cap.log.filter((x) => x.method === "POST" && x.url.includes("force-logout")).length !== 1) {
  throw new Error("force logout must POST once");
}
await waitCpHidden(document);

void window.cpAlert({ title: "کد", body: "test", codeDisplay: "1234" });
await waitCpVisible(document);
const okBtn = [...document.querySelectorAll(".cp-foot .cp-btn")].find((b) => b.textContent.trim() === "ثبت");
if (!okBtn) throw new Error("cpAlert must offer ثبت");
if (document.activeElement !== okBtn) throw new Error("focus should be on ثبت");
okBtn.click();
await waitCpHidden(document);

console.log("cp-d3-confirm-alert-selftest: ok");