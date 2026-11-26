import { loadOperatorApp } from "./operator-jsdom-harness.mjs";

const payload = '<img src=x onerror="window.__xss=1">';
const { document, window, runInApp } = loadOperatorApp();
delete window.__xss;

globalThis.renderCloseSessions([
  {
    sessionId: "11111111-1111-4111-8111-111111111111",
    stationId: payload,
    status: payload,
  },
]);

const list = document.getElementById("closeSessionList");
if (list?.querySelector("img")) throw new Error("close-session list must not create img");

globalThis.renderReportsTable("t", ["h"], [[payload, payload]]);
document.getElementById("reportsOut").innerHTML = globalThis.renderReportsTable("t", ["h"], [[payload]]);
if (document.getElementById("reportsOut").querySelector("img")) throw new Error("reports table must not create img");

const csvPre = `<pre>${globalThis.escapeHtmlText(payload)}</pre>`;
document.getElementById("reportsOut").innerHTML = `<h3>خروجی CSV</h3>${csvPre}`;
if (document.querySelector("pre img")) throw new Error("csv pre must not create img");

if (window.__xss !== undefined) throw new Error("xss payload executed");

const mutantAllowsImg = runInApp(`(function () {
  const payload = ${JSON.stringify(payload)};
  const old = escapeHtmlText;
  escapeHtmlText = (s) => s;
  try {
    const html = renderReportsTable("t", ["h"], [[payload]]);
    const el = document.createElement("div");
    el.innerHTML = html;
    return el.querySelector("img") !== null;
  } finally {
    escapeHtmlText = old;
  }
})()`);
if (!mutantAllowsImg) throw new Error("D5 mutation harness must detect identity escapeHtmlText");

console.log("cp-d5-xss-render-selftest: ok");