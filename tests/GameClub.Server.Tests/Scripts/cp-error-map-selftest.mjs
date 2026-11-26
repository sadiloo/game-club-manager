import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const layerJs = fs.readFileSync(
  path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../src/GameClub.Server/wwwroot/operator/cp-layer.js"),
  "utf8",
);
const ctx = {
  console,
  window: { addEventListener() {}, matchMedia: () => ({ matches: true }) },
  document: { getElementById: () => null, body: {} },
};
vm.createContext(ctx);
vm.runInContext(layerJs, ctx);

const msg = ctx.window.cpTranslateApiError("reason_required", "Reason is required");
if (!msg.includes("دلیل")) throw new Error("expected Persian mapping");
const mappedWins = ctx.window.cpTranslateApiError("invalid_money", "Payment amounts must be positive.");
if (mappedWins !== "مبلغ درست نیست.") {
  throw new Error("mapped ERROR_MAP code must win over raw English server message");
}
const voidForbidden = ctx.window.cpTranslateApiError("forbidden", "Managers only");
if (!voidForbidden.includes("فاکتور")) throw new Error("forbidden void invoice text");
const ladderForbidden = ctx.window.cpTranslateApiError("forbidden", "Managers only", { scope: "level_ladder" });
if (!ladderForbidden.includes("جدول سطح")) throw new Error("forbidden ladder text");
if (ladderForbidden.includes("فاکتور")) throw new Error("ladder forbidden must not use void text");
const unknown = ctx.window.cpTranslateApiError("xyz_unknown", "Something broke");
if (/[A-Za-z]/.test(unknown)) throw new Error("unknown should not leak Latin");
const persianFallback = ctx.window.cpTranslateApiError(
  "unmapped_code",
  "ساعت بازی یا سطح را وارد کنید، نه هر دو.",
);
if (persianFallback !== "ساعت بازی یا سطح را وارد کنید، نه هر دو.") {
  throw new Error("unmapped code must fall back to Persian server message");
}
if (ctx.window.cpTranslateApiError("invalid_opening", "") !== "مانده افتتاحیه درست نیست.") {
  throw new Error("invalid_opening fallback");
}
const invalidAmountGift = ctx.window.cpTranslateApiError("invalid_amount", "", { scope: "member_gift" });
if (invalidAmountGift !== "هدیه نمی‌تواند منفی باشد.") throw new Error("invalid_amount gift scope");
if (ctx.window.cpTranslateApiError("invalid_amount", "") !== "مبلغ نامعتبر است.") throw new Error("invalid_amount default");
if (ctx.window.cpTranslateApiError("no_open_shift", "x") !== "شیفتی باز نیست. اول شیفت را باز کنید.") {
  throw new Error("missing no_open_shift code");
}
if (ctx.window.cpTranslateApiError("invalid_time", "x") !== "زمان درست نیست.") throw new Error("invalid_time text");
if (ctx.window.cpTranslateApiError("invalid_minutes", "x") !== "دقیقه باید بیشتر از صفر باشد.") {
  throw new Error("invalid_minutes text");
}
const paymentInvalidServer = ctx.window.cpTranslateApiError(
  "payment_invalid",
  "جمع پرداختی با مبلغ قابل پرداخت برابر نیست.",
);
if (paymentInvalidServer !== "جمع پرداختی با مبلغ قابل پرداخت برابر نیست.") {
  throw new Error("payment_invalid must prefer server Persian over ERROR_MAP");
}
const paymentInvalidFallback = ctx.window.cpTranslateApiError("payment_invalid", "");
if (paymentInvalidFallback !== "دست‌کم یک خط پرداخت با مبلغ بیشتر از صفر لازم است.") {
  throw new Error("payment_invalid must use ERROR_MAP when server sends no message");
}
const paymentInvalidLatin = ctx.window.cpTranslateApiError("payment_invalid", "Sum mismatch");
if (paymentInvalidLatin !== "دست‌کم یک خط پرداخت با مبلغ بیشتر از صفر لازم است.") {
  throw new Error("payment_invalid must use ERROR_MAP when server message is not Persian");
}

console.log("cp-error-map-selftest: ok");