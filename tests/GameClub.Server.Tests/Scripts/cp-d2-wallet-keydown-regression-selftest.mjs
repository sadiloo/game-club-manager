import {
  loadOperatorApp,
  installFetchCapture,
  sleep,
  fillMoneyField,
  clickCpSubmit,
  clickCpCancel,
  clickCpClose,
  waitCpVisible,
  waitCpHidden,
  dispatchKey,
  lastPost,
  assertPostsCount,
} from "./operator-jsdom-harness.mjs";

const CUSTOMER = "11111111-1111-4111-8111-111111111111";

async function openClosePrompt(document, role) {
  void window.cpPrompt({
    title: "t",
    field: { kind: "money", label: "m", positive: true },
  });
  await waitCpVisible(document);
  if (role === "cancel") await clickCpCancel(document);
  else await clickCpClose(document);
  await waitCpHidden(document);
}

const { window, document, runInApp, ctx } = loadOperatorApp("http://localhost/operator/", { trackKeydown: true });
const cap = installFetchCapture(window, ctx);
cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet`, "GET", { realToman: 0, giftToman: 0, spendableToman: 0 });
runInApp(`displayedLinkedCustomer = { id: "${CUSTOMER}", name: "T" };`);

if (window.__cpKeydownListenerCount() !== 0) throw new Error("expected 0 listeners at start");

await openClosePrompt(document, "close");
await openClosePrompt(document, "cancel");
await openClosePrompt(document, "close");

if (window.__cpKeydownListenerCount() !== 0) throw new Error("after 3 cycles expected 0 listeners");

cap.hangingPost("/wallet/top-up");
void globalThis.walletTopUp("Cash");
await waitCpVisible(document);
if (window.__cpKeydownListenerCount() !== 1) throw new Error(`wallet open expected 1 listener, got ${window.__cpKeydownListenerCount()}`);

await fillMoneyField(document, "amountToman", "7777");
dispatchKey(window, "Enter", { ctrl: true });
await sleep(40);
dispatchKey(window, "Enter");
await sleep(40);
const submit = document.querySelector('[data-cp-role="submit"]');
submit?.click();
submit?.click();
await sleep(120);

assertPostsCount(cap.log, "/wallet/top-up", 1);
const post = lastPost(cap.log, "/wallet/top-up");
if (post?.body?.amountToman !== 7777) throw new Error(`wrong top-up amount ${post?.body?.amountToman}`);

await waitCpHidden(document, 8000);
if (window.__cpKeydownListenerCount() !== 0) throw new Error("after wallet close expected 0 listeners");

console.log("cp-d2-wallet-keydown-regression-selftest: ok");