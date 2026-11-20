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
  if (!String(window.location.href).includes("/operator/")) throw new Error(`left operator: ${window.location.href}`);
}

const { window, document, runInApp, ctx } = loadOperatorApp();
const cap = installFetchCapture(window, ctx);
cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet`, "GET", { realToman: 0, giftToman: 0, spendableToman: 0 });
runInApp(`displayedLinkedCustomer = { id: "${CUSTOMER}", name: "T" };`);

void window.cpPrompt({ title: "x", field: { kind: "money", label: "m", positive: true } });
await waitCpVisible(document);
window.history.back();
await sleep(80);
await waitCpHidden(document);
assertOnOperator(window);

cap.stub(`/api/operator/members/customers/${CUSTOMER}/wallet/top-ups`, "GET", [
  {
    topUpGroupId: TOPUP_GROUP,
    method: "Cash",
    currentAmountToman: 1000,
    currentGiftToman: 0,
    atUtc: "2025-01-01T00:00:00Z",
  },
]);
void globalThis.walletCorrectTopUp();
await waitCpVisible(document);
document.querySelector(".cp-pick-row")?.click();
await waitCpVisible(document);
await fillMoneyField(document, "newAmountToman", "2000");
await fillMoneyField(document, "newGiftToman", "0");
document.querySelector('[data-cp-field="description"]').value = "back test";
cap.reset();
cap.stub(`/api/operator/members/wallet/top-ups/${TOPUP_GROUP}/correct`, "POST", {});
await clickCpSubmit(document);
await sleep(200);
await waitCpHidden(document);
assertOnOperator(window);

console.log("cp-d4-popstate-back-selftest: ok");