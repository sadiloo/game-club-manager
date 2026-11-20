import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { JSDOM } from "jsdom";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../../../src/GameClub.Server/wwwroot/operator");

const layerJs = fs.readFileSync(path.join(root, "cp-layer.js"), "utf8");
const appJs = fs.readFileSync(path.join(root, "app.js"), "utf8");

export function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

const LAYER_GLOBALS = [
  "cpForm",
  "cpPrompt",
  "cpConfirm",
  "cpAlert",
  "cpPick",
  "cpToast",
  "cpParseNumber",
  "cpParseInteger",
  "cpParseNumberResult",
  "cpParseIntegerResult",
  "cpTranslateApiError",
  "cpLayer",
];

const APP_FNS = [
  "walletTopUp",
  "walletCorrectTopUp",
  "renderBuffetCategories",
  "ss14LoadManagerData",
  "ss14SaveManagerTitle",
  "ss14EnsureManagerPanel",
  "ss14RenderManagerForm",
  "renderCloseSessions",
  "renderReportsTable",
  "escapeHtmlText",
];

export function loadOperatorApp(url = "http://localhost/operator/", { trackKeydown = false } = {}) {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const dom = new JSDOM(html, { url, pretendToBeVisual: true });
  const { window } = dom;
  window.matchMedia = () => ({ matches: false });
  window.scrollTo = () => {};
  if (!window.crypto?.randomUUID) {
    window.crypto = {
      randomUUID: () => "11111111-1111-4111-8111-111111111111",
    };
  }
  const intervalIds = [];
  const origSetInterval = window.setInterval.bind(window);
  window.setInterval = (fn, ms) => {
    const id = origSetInterval(fn, ms);
    intervalIds.push(id);
    return id;
  };
  window.__prepaidRefreshIntervalMs = 60_000_000;

  let keydownListeners = 0;
  if (trackKeydown) {
    const origAdd = window.Element.prototype.addEventListener;
    const origRemove = window.Element.prototype.removeEventListener;
    window.Element.prototype.addEventListener = function (type, fn, opts) {
      if (type === "keydown" && this.id === "cpLayer") keydownListeners++;
      return origAdd.call(this, type, fn, opts);
    };
    window.Element.prototype.removeEventListener = function (type, fn, opts) {
      if (type === "keydown" && this.id === "cpLayer") keydownListeners--;
      return origRemove.call(this, type, fn, opts);
    };
    window.__cpKeydownListenerCount = () => keydownListeners;
  }

  const nativeFail = () => {
    throw new Error("native dialog forbidden");
  };
  window.prompt = nativeFail;
  window.alert = nativeFail;
  window.confirm = nativeFail;

  const ctx = vm.createContext({
    console,
    setTimeout,
    clearTimeout,
    setInterval: window.setInterval.bind(window),
    clearInterval: window.clearInterval.bind(window),
    Promise,
    window,
    document: window.document,
    history: window.history,
    fetch: async () => ({ ok: true, status: 200, text: async () => "null" }),
  });

  vm.runInContext(layerJs, ctx);
  const bridge = LAYER_GLOBALS.map((g) => `var ${g} = window.${g};`).join("\n");
  vm.runInContext(bridge, ctx);
  vm.runInContext(appJs, ctx);

  for (const id of intervalIds) window.clearInterval(id);

  const runInApp = (expr) => vm.runInContext(expr, ctx);
  for (const name of APP_FNS) {
    if (typeof ctx[name] === "function") {
      globalThis[name] = (...args) => ctx[name](...args);
    }
  }
  globalThis.window = window;
  globalThis.document = window.document;

  return { window, document: window.document, history: window.history, dom, runInApp, ctx };
}

export function runInApp(expr) {
  throw new Error("runInApp requires loadOperatorApp() return value: const { runInApp } = loadOperatorApp()");
}

export function installFetchCapture(window, ctx) {
  const log = [];
  const responses = new Map();
  const defaultOk = {
    ok: true,
    status: 200,
    text: async () => "null",
    json: async () => null,
  };
  const fetchImpl = async (url, opts = {}) => {
    let body = null;
    if (opts.body) {
      try {
        body = JSON.parse(opts.body);
      } catch {
        body = opts.body;
      }
    }
    log.push({ url: String(url), method: opts.method || "GET", body });
    const pathOnly = String(url);
    const key = `${opts.method || "GET"} ${pathOnly}`;
    const stub = responses.get(key) ?? responses.get(pathOnly);
    if (stub) return stub;
    return defaultOk;
  };
  window.fetch = fetchImpl;
  globalThis.fetch = fetchImpl;
  if (ctx) ctx.fetch = fetchImpl;
  return {
    log,
    reset() {
      log.length = 0;
    },
    stub(pathOrKey, method, value) {
      if (typeof value === "undefined" && typeof method !== "string") {
        responses.set(pathOrKey, method);
        return;
      }
      responses.set(`${method} ${pathOrKey}`, {
        ok: true,
        status: 200,
        text: async () => JSON.stringify(value ?? {}),
        json: async () => value ?? {},
      });
    },
    stubJsonError(pathOrKey, method, status, body) {
      responses.set(`${method} ${pathOrKey}`, {
        ok: false,
        status,
        text: async () => JSON.stringify(body),
        json: async () => body,
      });
    },
    hangingPost(pathPart) {
      responses.set(`POST ${pathPart}`, {
        ok: true,
        status: 200,
        text: () => new Promise(() => {}),
      });
    },
  };
}

export async function fillMoneyField(document, name, value) {
  const input = document.querySelector(`[data-cp-field="${name}"]`);
  if (!input) throw new Error(`missing money/field ${name}`);
  input.value = value;
}

export async function fillNumberField(document, name, value) {
  return fillMoneyField(document, name, value);
}

export async function fillCpPromptValue(document, value) {
  const input = document.querySelector('input[data-cp-field="value"]');
  if (!input) throw new Error("missing prompt value field");
  input.value = value;
}

export async function clickCpSubmit(document) {
  const btn = document.querySelector('[data-cp-role="submit"]');
  if (!btn) throw new Error("no submit");
  btn.click();
}

export async function clickCpCancel(document) {
  const btn = document.querySelector('[data-cp-role="cancel"]');
  if (btn) btn.click();
}

export async function clickCpClose(document) {
  const btn = document.querySelector('[data-cp-role="close"]');
  if (btn) btn.click();
}

export async function waitCpHidden(document, timeoutMs = 5000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const el = document.getElementById("cpLayer");
    if (!el || el.classList.contains("hidden")) return;
    await sleep(15);
  }
  throw new Error("cpLayer still visible");
}

export async function waitCpVisible(document, timeoutMs = 5000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const el = document.getElementById("cpLayer");
    if (el && !el.classList.contains("hidden")) return;
    await sleep(15);
  }
  throw new Error("cpLayer not visible");
}

export function assertNumberFields(body, expected) {
  for (const [key, val] of Object.entries(expected)) {
    if (typeof body[key] !== "number") throw new Error(`${key} must be number, got ${JSON.stringify(body[key])}`);
    if (body[key] !== val) throw new Error(`${key} expected ${val}, got ${body[key]}`);
  }
}

export function lastPost(log, pathPart, method = "POST") {
  const posts = log.filter((x) => x.method === method && String(x.url).includes(pathPart));
  return posts[posts.length - 1] ?? null;
}

export function assertPostsCount(log, pathPart, count) {
  const n = log.filter((x) => x.method === "POST" && String(x.url).includes(pathPart)).length;
  if (n !== count) throw new Error(`expected ${count} POSTs to ${pathPart}, got ${n}`);
}

export function dispatchKey(window, key, opts = {}) {
  const root = window.document.getElementById("cpLayer");
  const target = root?.querySelector(".cp-sheet") || root || window.document.body;
  target.dispatchEvent(
    new window.KeyboardEvent("keydown", {
      key,
      bubbles: true,
      cancelable: true,
      ctrlKey: !!opts.ctrl,
      metaKey: !!opts.meta,
    }),
  );
}