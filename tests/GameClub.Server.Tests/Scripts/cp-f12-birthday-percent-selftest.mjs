import {
  loadOperatorApp,
  installFetchCapture,
  sleep,
  fillNumberField,
  clickCpSubmit,
  waitCpVisible,
} from "./operator-jsdom-harness.mjs";

const ERR_PERCENT = "درصد تولد باید بین ۰ و ۱۰۰ باشد.";
const ERR_PERCENT_EMPTY = "درصد تولد را وارد کنید.";
const BIRTHDAY_PUT = "/api/operator/members/settings/birthday";

function percentFieldError(document) {
  const wrap = document.querySelector('[data-cp-field="percent"]')?.closest(".cp-field");
  const err = wrap?.querySelector(".cp-field-error");
  return err && !err.classList.contains("hidden") ? err : null;
}

function putBirthdayCalls(cap) {
  return cap.log.filter((x) => x.method === "PUT" && x.url.includes(BIRTHDAY_PUT));
}

{
  const { window, document, ctx } = loadOperatorApp();
  const cap = installFetchCapture(window, ctx);
  cap.stub(BIRTHDAY_PUT, "GET", {
    percent: 10,
    windowKind: "birth_month",
    windowDays: 3,
    anchor: "on_and_after",
  });
  document.getElementById("mgrBirthdayBtn")?.click();
  await waitCpVisible(document);
  const input = document.querySelector('[data-cp-field="percent"]');
  if (input) input.value = "";
  cap.reset();
  await clickCpSubmit(document);
  await sleep(40);
  if (putBirthdayCalls(cap).length !== 0) {
    throw new Error("empty percent must not call PUT birthday");
  }
  const errEmpty = percentFieldError(document);
  if (!errEmpty || errEmpty.textContent !== ERR_PERCENT_EMPTY) {
    throw new Error(`expected empty percent error, got: ${errEmpty?.textContent}`);
  }
  document.querySelector('[data-cp-role="cancel"]')?.click();
  await sleep(30);
}

{
  const { window, document, ctx } = loadOperatorApp();
  const cap = installFetchCapture(window, ctx);
  cap.stub(BIRTHDAY_PUT, "GET", {
    percent: 10,
    windowKind: "birth_month",
    windowDays: 3,
    anchor: "on_and_after",
  });
  document.getElementById("mgrBirthdayBtn")?.click();
  await waitCpVisible(document);
  await fillNumberField(document, "percent", "150");
  cap.reset();
  await clickCpSubmit(document);
  await sleep(40);
  if (putBirthdayCalls(cap).length !== 0) {
    throw new Error("out-of-range percent must not call PUT birthday");
  }
  const err = percentFieldError(document);
  if (!err || err.textContent !== ERR_PERCENT) {
    throw new Error(`expected inline percent error, got: ${err?.textContent}`);
  }
  document.querySelector('[data-cp-role="cancel"]')?.click();
  await sleep(30);
}

{
  const { window, document, ctx } = loadOperatorApp();
  const cap = installFetchCapture(window, ctx);
  cap.stub(BIRTHDAY_PUT, "GET", {
    percent: 10,
    windowKind: "birth_month",
    windowDays: 3,
    anchor: "on_and_after",
  });
  cap.stubJsonError(BIRTHDAY_PUT, "PUT", 400, { code: "invalid_policy", message: ERR_PERCENT });

  document.getElementById("mgrBirthdayBtn")?.click();
  await waitCpVisible(document);
  await fillNumberField(document, "percent", "50");
  cap.reset();
  await clickCpSubmit(document);
  await sleep(80);
  if (putBirthdayCalls(cap).length !== 1) {
    throw new Error("valid percent should submit PUT once");
  }
  const err = percentFieldError(document);
  if (!err || err.textContent !== ERR_PERCENT) {
    throw new Error(`server 400 should map to percent field, got: ${err?.textContent}`);
  }
  const layer = document.getElementById("cpLayer");
  if (!layer || layer.classList.contains("hidden")) {
    throw new Error("form should stay open after server field error");
  }
}

console.log("cp-f12-birthday-percent-selftest: ok");