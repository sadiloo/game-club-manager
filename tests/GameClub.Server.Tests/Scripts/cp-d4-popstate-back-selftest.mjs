import {
  loadOperatorApp,
  installFetchCapture,
  sleep,
  waitCpVisible,
  waitCpHidden,
  fillMoneyField,
  clickCpSubmit,
} from "./operator-jsdom-harness.mjs";

const CUSTOMER = "11111111-1111-4111-8111-111111111111";
const TOPUP_GROUP = "33333333-3333-4333-8333-333333333333";

function assertOnOperator(window) {
  if (!String(window.location.href).includes("/operator/")) {
    throw new Error(`left operator: ${window.location.href}`);
  }
}

async function runViewport({ phone }) {
  const baseUrl = phone ? "http://localhost/operator/?phone=1" : "http://localhost/operator/";
  const { window, document, history, runInApp, ctx } = loadOperatorApp(baseUrl, {
    trackKeydown: false,
  });
  if (phone) {
    window.matchMedia = (q) => ({ matches: String(q).includes("max-width") });
  }

  let programmaticBacks = 0;
  const nativeBack = history.back.bind(history);
  history.back = () => {
    programmaticBacks += 1;
    return nativeBack();
  };

  const cap = installFetchCapture(window, ctx);
  cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet`, "GET", { realToman: 0, giftToman: 0, spendableToman: 0 });
  runInApp(`displayedLinkedCustomer = { id: "${CUSTOMER}", name: "T" };`);

  const startLen = history.length;
  void window.cpPrompt({ title: "x", field: { kind: "money", label: "m", positive: true } });
  await waitCpVisible(document);
  if (history.length <= startLen) throw new Error("dialog must push history");
  programmaticBacks = 0;
  history.back();
  await sleep(80);
  await waitCpHidden(document);
  assertOnOperator(window);
  if (programmaticBacks !== 1) {
    throw new Error(`popstate must perform exactly one history.back (got ${programmaticBacks})`);
  }

  cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet/top-ups`, "GET", [
    {
      topUpGroupId: TOPUP_GROUP,
      method: "Cash",
      currentAmountToman: 1000,
      currentGiftToman: 0,
      atUtc: "2025-01-01T00:00:00Z",
    },
  ]);
  programmaticBacks = 0;
  void globalThis.walletCorrectTopUp();
  await waitCpVisible(document);
  document.querySelector(".cp-pick-row")?.click();
  await waitCpVisible(document);

  cap.reset();
  cap.stub(`/api/operator/members/wallet/top-ups/${TOPUP_GROUP}/correct`, "POST", {});
  await fillMoneyField(document, "newAmountToman", "2000");
  await fillMoneyField(document, "newGiftToman", "0");
  document.querySelector('[data-cp-field="description"]').value = "back test";
  programmaticBacks = 0;
  await clickCpSubmit(document);
  await sleep(200);
  await waitCpHidden(document);
  assertOnOperator(window);
  if (programmaticBacks < 1) {
    throw new Error(`wallet correction submit must pop cp history (got ${programmaticBacks})`);
  }

  void globalThis.walletCorrectTopUp();
  await waitCpVisible(document);
  document.querySelector(".cp-pick-row")?.click();
  await waitCpVisible(document);
  programmaticBacks = 0;
  document.querySelector(".cp-secondary")?.click();
  await sleep(200);
  await waitCpHidden(document);
  assertOnOperator(window);
  if (programmaticBacks > 0) {
    throw new Error(`wallet correction cancel must not history.back (got ${programmaticBacks})`);
  }
}

await runViewport({ phone: false });
await runViewport({ phone: true });

console.log("cp-d4-popstate-back-selftest: ok");