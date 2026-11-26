using GameClub.Kiosk.Core.Admin;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskOpStringsTests
{
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["kiosk.free.tag"] = "رایگان",
        ["kiosk.free.banner"] =
            "استفادهٔ رایگان است و هزینه‌ای ثبت نمی‌شود. کارتان که تمام شد، لطفاً سیستم را قفل کنید.",
        ["kiosk.free.bannerIdle"] =
            "اگر {N} دقیقه با صفحه‌کلید یا ماوس کاری نشود، سیستم خودکار قفل می‌شود.",
        ["kiosk.free.bannerWarn"] =
            "سیستم تا {s} ثانیهٔ دیگر قفل می‌شود. برای ادامه، ماوس را تکان دهید یا کلیدی را بزنید.",
        ["kiosk.free.bannerWarnCancelled"] = "قفل خودکار لغو شد.",
        ["kiosk.free.a11yName"] = "استفادهٔ رایگان",
        ["admin.pcUnlock.busy"] = "در حال باز کردن قفل…",
        ["admin.pcUnlock.done"] = "قفل باز شد. استفادهٔ رایگان شروع شد.",
        ["admin.pcLock.busy"] = "در حال قفل…",
        ["admin.pcLock.done"] = "سیستم قفل شد.",
        ["admin.pcLock.confirm.title"] = "قفل و پایان جلسه؟",
        ["admin.pcLock.confirm.body"] =
            "جلسهٔ این سیستم همین حالا تمام می‌شود و صورتحساب برای پرداخت به پیشخوان می‌رود.",
        ["admin.pcLock.confirm.btn"] = "قفل و پایان جلسه",
        ["admin.pcLock.session.busy"] = "در حال پایان جلسه…",
        ["admin.pcLock.session.done"] = "جلسه تمام شد و سیستم قفل شد. صورتحساب در پیشخوان است.",
        ["admin.pcUnlock.unavailable"] = "این سیستم الان باز نمی‌شود: {دلیل}.",
        ["admin.pcUnlock.reason.awaiting"] = "منتظر تسویه است",
        ["admin.pcUnlock.reason.broken"] = "خراب ثبت شده است",
        ["admin.pcUnlock.reason.unpaired"] = "هنوز جفت نشده است",
        ["admin.pcLock.offline"] = "به سرور وصل نیست؛ باز کردن قفل فعلاً انجام نمی‌شود.",
        ["admin.pcLock.offlineFreeUse"] =
            "به سرور وصل نیست. قفل کردن کار می‌کند و پایان استفاده بعد از وصل شدن ثبت می‌شود.",
        ["admin.pcLock.offlineSession"] =
            "به سرور وصل نیست؛ پایان جلسه از اینجا فعلاً انجام نمی‌شود و جلسه ادامه دارد.",
        ["admin.pcLock.doneOffline"] =
            "سیستم قفل شد. پایان استفاده بعد از وصل شدن به سرور ثبت می‌شود.",
        ["kiosk.oplock.err.offline"] =
            "به سرور وصل نیست، پس سیستم قفل نشد و جلسه ادامه دارد. کمی بعد دوباره امتحان کنید.",
        ["kiosk.freeuse.err.offline"] =
            "به سرور وصل نیست، پس استفادهٔ رایگان شروع نشد و سیستم قفل ماند. کمی بعد دوباره امتحان کنید.",
        ["admin.pcLock.err.stale"] = "وضعیت این سیستم عوض شده است. دوباره نگاه کنید و امتحان کنید.",
        ["admin.pcLock.err.generic"] =
            "انجام نشد. دوباره امتحان کنید؛ اگر تکرار شد، به پیشخوان خبر بدهید.",
        ["panel.tile.freeUse"] = "رایگان",
        ["panel.tile.freeUse.noContact"] = "بی‌ارتباط",
        ["panel.tile.freeUse.lastContact"] = "آخرین ارتباط: {زمان}",
        ["panel.tile.freeUse.noContactNote"] =
            "سرور بیش از ۳ دقیقه است از این سیستم خبر ندارد. زمان تا آخرین ارتباط حساب شده است.",
        ["panel.tile.freeUse.noContact.a11y"] = "⁨{PC}⁩، رایگان، بی‌ارتباط، {m} دقیقه",
        ["panel.tile.freeUse.a11y"] = "⁨{PC}⁩، رایگان، {m} دقیقه",
        ["panel.freeUse.sheet.title"] = "⁨{PC}⁩ — استفادهٔ رایگان",
        ["panel.freeUse.sheet.start"] = "شروع: {HH:mm}",
        ["panel.freeUse.sheet.elapsed"] = "زمان گذشته: {HH:mm}",
        ["panel.freeUse.sheet.note"] =
            "پایان استفادهٔ رایگان فقط با «قفل» روی خود سیستم است، یا خودکار بعد از {N} دقیقه بی‌حرکتی.",
        ["panel.freeUse.sheet.close"] = "بستن",
        ["mgr.freeIdle.sentence"] = "قفل خودکار استفادهٔ رایگان بعد از {فیلد} دقیقه بی‌حرکتی",
        ["mgr.freeIdle.label"] = "قفل خودکار استفادهٔ رایگان بعد از چند دقیقه بی‌حرکتی",
        ["mgr.freeIdle.help"] = "پیش‌فرض ۵ دقیقه. عدد کامل از ۱ تا ۲۴۰.",
        ["mgr.freeIdle.applyNote"] =
            "مقدار تازه با تماس بعدی هر کیوسک با سرور اعمال می‌شود، حتی روی استفاده‌های رایگانِ باز.",
        ["mgr.freeIdle.save"] = "ذخیره",
        ["mgr.freeIdle.saving"] = "در حال ذخیره…",
        ["mgr.freeIdle.saved"] = "ذخیره شد.",
        ["mgr.freeIdle.err.empty"] = "تعداد دقیقه را وارد کنید.",
        ["mgr.freeIdle.err.invalid"] = "یک عدد کامل از ۱ تا ۲۴۰ وارد کنید.",
        ["mgr.freeIdle.err.forbidden"] = "فقط مدیر یا مالک می‌تواند این را عوض کند.",
        ["mgr.freeIdle.err.saveFail"] = "ذخیره نشد. دوباره امتحان کنید.",
        ["mgr.freeIdle.loadFail"] = "تنظیم خوانده نشد.",
        ["common.retry"] = "تلاش دوباره",
        ["rep.freeUse.title"] = "استفادهٔ رایگان کیوسک",
        ["rep.freeUse.btn"] = "استفادهٔ رایگان کیوسک",
        ["rep.freeUse.note"] = "این استفاده‌ها رایگان‌اند و در درآمد و جمع دریافتی حساب نمی‌شوند.",
        ["rep.freeUse.col.pc"] = "PC",
        ["rep.freeUse.col.operator"] = "اپراتور کیوسک",
        ["rep.freeUse.col.start"] = "شروع",
        ["rep.freeUse.col.end"] = "پایان",
        ["rep.freeUse.col.duration"] = "مدت",
        ["rep.freeUse.col.reason"] = "علت پایان",
        ["rep.freeUse.role.operator"] = "اپراتور کیوسک",
        ["rep.freeUse.role.manager"] = "مدیر کیوسک",
        ["rep.freeUse.reason.manual"] = "دستی",
        ["rep.freeUse.reason.auto"] = "خودکار",
        ["rep.freeUse.reason.restart"] = "خاموش/ری‌استارت",
        ["rep.freeUse.reason.open"] = "در جریان",
        ["rep.freeUse.reason.noContact"] = "بی‌ارتباط",
        ["rep.freeUse.restartNote"] =
            "برای «خاموش/ری‌استارت»، زمان پایان آخرین زمان فعالیت ثبت‌شدهٔ همان سیستم است.",
        ["rep.freeUse.duration.toLastContact"] = "{مدت} تا آخرین ارتباط",
        ["rep.freeUse.noContactNote"] =
            "برای «بی‌ارتباط»، مدت تا آخرین ارتباط سیستم با سرور حساب شده است.",
        ["rep.freeUse.totalNoContact"] =
            "({j} مورد بی‌ارتباط است و تا آخرین ارتباط حساب شده است.)",
        ["rep.freeUse.total"] = "{n} مورد — مجموع مدت: {مدت}",
        ["rep.freeUse.totalOpen"] = "({k} مورد هنوز باز است و تا همین حالا حساب شده است.)",
        ["rep.freeUse.dur.hm"] = "{h} ساعت و {m} دقیقه",
        ["rep.freeUse.dur.m"] = "{m} دقیقه",
        ["rep.freeUse.dur.lt1"] = "کمتر از ۱ دقیقه",
        ["rep.freeUse.dur.sofar"] = "{مدت} تا الان",
        ["rep.freeUse.card.operator"] = "اپراتور کیوسک: {…}",
        ["rep.freeUse.card.start"] = "شروع: {…}",
        ["rep.freeUse.card.end"] = "پایان: {…}",
        ["rep.freeUse.card.duration"] = "مدت: {…}",
        ["rep.freeUse.loading"] = "در حال بارگذاری…",
        ["rep.freeUse.empty"] = "در این بازه استفادهٔ رایگانی ثبت نشده است.",
        ["rep.freeUse.error"] = "گزارش بارگذاری نشد. دوباره امتحان کنید.",
        ["rep.freeUse.badRange"] = "تاریخ شروع بازه بعد از تاریخ پایان است.",
        ["rep.freeUse.forbidden"] = "این گزارش فقط برای مدیر یا مالک است.",
    };

    [Fact]
    public void every_k_op_key_matches_spec_text()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kiosk.free.tag"] = KioskOpStrings.FreeTag,
            ["kiosk.free.banner"] = KioskOpStrings.FreeBanner,
            ["kiosk.free.bannerIdle"] = KioskOpStrings.FreeBannerIdle,
            ["kiosk.free.bannerWarn"] = KioskOpStrings.FreeBannerWarn,
            ["kiosk.free.bannerWarnCancelled"] = KioskOpStrings.FreeBannerWarnCancelled,
            ["kiosk.free.a11yName"] = KioskOpStrings.FreeA11yName,
            ["admin.pcUnlock.busy"] = KioskOpStrings.AdminPcUnlockBusy,
            ["admin.pcUnlock.done"] = KioskOpStrings.AdminPcUnlockDone,
            ["admin.pcLock.busy"] = KioskOpStrings.AdminPcLockBusy,
            ["admin.pcLock.done"] = KioskOpStrings.AdminPcLockDone,
            ["admin.pcLock.confirm.title"] = KioskOpStrings.AdminPcLockConfirmTitle,
            ["admin.pcLock.confirm.body"] = KioskOpStrings.AdminPcLockConfirmBody,
            ["admin.pcLock.confirm.btn"] = KioskOpStrings.AdminPcLockConfirmBtn,
            ["admin.pcLock.session.busy"] = KioskOpStrings.AdminPcLockSessionBusy,
            ["admin.pcLock.session.done"] = KioskOpStrings.AdminPcLockSessionDone,
            ["admin.pcUnlock.unavailable"] = KioskOpStrings.AdminPcUnlockUnavailable,
            ["admin.pcUnlock.reason.awaiting"] = KioskOpStrings.AdminPcUnlockReasonAwaiting,
            ["admin.pcUnlock.reason.broken"] = KioskOpStrings.AdminPcUnlockReasonBroken,
            ["admin.pcUnlock.reason.unpaired"] = KioskOpStrings.AdminPcUnlockReasonUnpaired,
            ["admin.pcLock.offline"] = KioskOpStrings.AdminPcLockOffline,
            ["admin.pcLock.offlineFreeUse"] = KioskOpStrings.AdminPcLockOfflineFreeUse,
            ["admin.pcLock.offlineSession"] = KioskOpStrings.AdminPcLockOfflineSession,
            ["admin.pcLock.doneOffline"] = KioskOpStrings.AdminPcLockDoneOffline,
            ["kiosk.oplock.err.offline"] = KioskOpStrings.OplockErrOffline,
            ["kiosk.freeuse.err.offline"] = KioskOpStrings.FreeuseErrOffline,
            ["admin.pcLock.err.stale"] = KioskOpStrings.AdminPcLockErrStale,
            ["admin.pcLock.err.generic"] = KioskOpStrings.AdminPcLockErrGeneric,
            ["panel.tile.freeUse"] = KioskOpStrings.PanelTileFreeUse,
            ["panel.tile.freeUse.noContact"] = KioskOpStrings.PanelTileFreeUseNoContact,
            ["panel.tile.freeUse.lastContact"] = KioskOpStrings.PanelTileFreeUseLastContact,
            ["panel.tile.freeUse.noContactNote"] = KioskOpStrings.PanelTileFreeUseNoContactNote,
            ["panel.tile.freeUse.noContact.a11y"] = KioskOpStrings.PanelTileFreeUseNoContactA11y,
            ["panel.tile.freeUse.a11y"] = KioskOpStrings.PanelTileFreeUseA11y,
            ["panel.freeUse.sheet.title"] = KioskOpStrings.PanelFreeUseSheetTitle,
            ["panel.freeUse.sheet.start"] = KioskOpStrings.PanelFreeUseSheetStart,
            ["panel.freeUse.sheet.elapsed"] = KioskOpStrings.PanelFreeUseSheetElapsed,
            ["panel.freeUse.sheet.note"] = KioskOpStrings.PanelFreeUseSheetNote,
            ["panel.freeUse.sheet.close"] = KioskOpStrings.PanelFreeUseSheetClose,
            ["mgr.freeIdle.sentence"] = KioskOpStrings.MgrFreeIdleSentence,
            ["mgr.freeIdle.label"] = KioskOpStrings.MgrFreeIdleLabel,
            ["mgr.freeIdle.help"] = KioskOpStrings.MgrFreeIdleHelp,
            ["mgr.freeIdle.applyNote"] = KioskOpStrings.MgrFreeIdleApplyNote,
            ["mgr.freeIdle.save"] = KioskOpStrings.MgrFreeIdleSave,
            ["mgr.freeIdle.saving"] = KioskOpStrings.MgrFreeIdleSaving,
            ["mgr.freeIdle.saved"] = KioskOpStrings.MgrFreeIdleSaved,
            ["mgr.freeIdle.err.empty"] = KioskOpStrings.MgrFreeIdleErrEmpty,
            ["mgr.freeIdle.err.invalid"] = KioskOpStrings.MgrFreeIdleErrInvalid,
            ["mgr.freeIdle.err.forbidden"] = KioskOpStrings.MgrFreeIdleErrForbidden,
            ["mgr.freeIdle.err.saveFail"] = KioskOpStrings.MgrFreeIdleErrSaveFail,
            ["mgr.freeIdle.loadFail"] = KioskOpStrings.MgrFreeIdleLoadFail,
            ["common.retry"] = KioskOpStrings.CommonRetry,
            ["rep.freeUse.title"] = KioskOpStrings.RepFreeUseTitle,
            ["rep.freeUse.btn"] = KioskOpStrings.RepFreeUseBtn,
            ["rep.freeUse.note"] = KioskOpStrings.RepFreeUseNote,
            ["rep.freeUse.col.pc"] = KioskOpStrings.RepFreeUseColPc,
            ["rep.freeUse.col.operator"] = KioskOpStrings.RepFreeUseColOperator,
            ["rep.freeUse.col.start"] = KioskOpStrings.RepFreeUseColStart,
            ["rep.freeUse.col.end"] = KioskOpStrings.RepFreeUseColEnd,
            ["rep.freeUse.col.duration"] = KioskOpStrings.RepFreeUseColDuration,
            ["rep.freeUse.col.reason"] = KioskOpStrings.RepFreeUseColReason,
            ["rep.freeUse.role.operator"] = KioskOpStrings.RepFreeUseRoleOperator,
            ["rep.freeUse.role.manager"] = KioskOpStrings.RepFreeUseRoleManager,
            ["rep.freeUse.reason.manual"] = KioskOpStrings.RepFreeUseReasonManual,
            ["rep.freeUse.reason.auto"] = KioskOpStrings.RepFreeUseReasonAuto,
            ["rep.freeUse.reason.restart"] = KioskOpStrings.RepFreeUseReasonRestart,
            ["rep.freeUse.reason.open"] = KioskOpStrings.RepFreeUseReasonOpen,
            ["rep.freeUse.reason.noContact"] = KioskOpStrings.RepFreeUseReasonNoContact,
            ["rep.freeUse.restartNote"] = KioskOpStrings.RepFreeUseRestartNote,
            ["rep.freeUse.duration.toLastContact"] = KioskOpStrings.RepFreeUseDurationToLastContact,
            ["rep.freeUse.noContactNote"] = KioskOpStrings.RepFreeUseNoContactNote,
            ["rep.freeUse.totalNoContact"] = KioskOpStrings.RepFreeUseTotalNoContact,
            ["rep.freeUse.total"] = KioskOpStrings.RepFreeUseTotal,
            ["rep.freeUse.totalOpen"] = KioskOpStrings.RepFreeUseTotalOpen,
            ["rep.freeUse.dur.hm"] = KioskOpStrings.RepFreeUseDurHm,
            ["rep.freeUse.dur.m"] = KioskOpStrings.RepFreeUseDurM,
            ["rep.freeUse.dur.lt1"] = KioskOpStrings.RepFreeUseDurLt1,
            ["rep.freeUse.dur.sofar"] = KioskOpStrings.RepFreeUseDurSofar,
            ["rep.freeUse.card.operator"] = KioskOpStrings.RepFreeUseCardOperator,
            ["rep.freeUse.card.start"] = KioskOpStrings.RepFreeUseCardStart,
            ["rep.freeUse.card.end"] = KioskOpStrings.RepFreeUseCardEnd,
            ["rep.freeUse.card.duration"] = KioskOpStrings.RepFreeUseCardDuration,
            ["rep.freeUse.loading"] = KioskOpStrings.RepFreeUseLoading,
            ["rep.freeUse.empty"] = KioskOpStrings.RepFreeUseEmpty,
            ["rep.freeUse.error"] = KioskOpStrings.RepFreeUseError,
            ["rep.freeUse.badRange"] = KioskOpStrings.RepFreeUseBadRange,
            ["rep.freeUse.forbidden"] = KioskOpStrings.RepFreeUseForbidden,
        };

        Assert.Equal(Expected.Count, map.Count);

        foreach (var (key, expected) in Expected)
        {
            Assert.True(map.TryGetValue(key, out var actual), $"Missing key {key}");
            Assert.Equal(expected, actual);
        }
    }

}