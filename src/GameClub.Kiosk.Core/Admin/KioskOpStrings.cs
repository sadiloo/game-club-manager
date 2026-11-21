namespace GameClub.Kiosk.Core.Admin;

/// <summary>Persian copy for K-OP (spec v2.9 §10).</summary>
public static class KioskOpStrings
{
    public const string FreeTag = "رایگان";
    public const string FreeBanner =
        "استفادهٔ رایگان است و هزینه‌ای ثبت نمی‌شود. کارتان که تمام شد، لطفاً سیستم را قفل کنید.";
    public const string FreeBannerIdle = "اگر {N} دقیقه با صفحه‌کلید یا ماوس کاری نشود، سیستم خودکار قفل می‌شود.";
    public const string FreeBannerWarn =
        "سیستم تا {s} ثانیهٔ دیگر قفل می‌شود. برای ادامه، ماوس را تکان دهید یا کلیدی را بزنید.";
    public const string FreeBannerWarnCancelled = "قفل خودکار لغو شد.";
    public const string FreeA11yName = "استفادهٔ رایگان";

    public const string AdminPcUnlockBusy = "در حال باز کردن قفل…";
    public const string AdminPcUnlockDone = "قفل باز شد. استفادهٔ رایگان شروع شد.";
    public const string AdminPcLockBusy = "در حال قفل…";
    public const string AdminPcLockDone = "سیستم قفل شد.";
    public const string AdminPcLockConfirmTitle = "قفل و پایان جلسه؟";
    public const string AdminPcLockConfirmBody =
        "جلسهٔ این سیستم همین حالا تمام می‌شود و صورتحساب برای پرداخت به پیشخوان می‌رود.";
    public const string AdminPcLockConfirmBtn = "قفل و پایان جلسه";
    public const string AdminPcLockSessionBusy = "در حال پایان جلسه…";
    public const string AdminPcLockSessionDone = "جلسه تمام شد و سیستم قفل شد. صورتحساب در پیشخوان است.";
    public const string AdminPcUnlockUnavailable = "این سیستم الان باز نمی‌شود: {دلیل}.";
    public const string AdminPcUnlockReasonAwaiting = "منتظر تسویه است";
    public const string AdminPcUnlockReasonBroken = "خراب ثبت شده است";
    public const string AdminPcUnlockReasonUnpaired = "هنوز جفت نشده است";
    public const string AdminPcLockOffline = "به سرور وصل نیست؛ باز کردن قفل فعلاً انجام نمی‌شود.";
    public const string AdminPcLockOfflineFreeUse =
        "به سرور وصل نیست. قفل کردن کار می‌کند و پایان استفاده بعد از وصل شدن ثبت می‌شود.";
    public const string AdminPcLockOfflineSession =
        "به سرور وصل نیست؛ پایان جلسه از اینجا فعلاً انجام نمی‌شود و جلسه ادامه دارد.";
    public const string AdminPcLockDoneOffline = "سیستم قفل شد. پایان استفاده بعد از وصل شدن به سرور ثبت می‌شود.";

    public const string OplockErrOffline =
        "به سرور وصل نیست، پس سیستم قفل نشد و جلسه ادامه دارد. کمی بعد دوباره امتحان کنید.";
    public const string FreeuseErrOffline =
        "به سرور وصل نیست، پس استفادهٔ رایگان شروع نشد و سیستم قفل ماند. کمی بعد دوباره امتحان کنید.";

    public const string AdminPcLockErrStale = "وضعیت این سیستم عوض شده است. دوباره نگاه کنید و امتحان کنید.";
    public const string AdminPcLockErrGeneric =
        "انجام نشد. دوباره امتحان کنید؛ اگر تکرار شد، به پیشخوان خبر بدهید.";

    public const string PanelTileFreeUse = "رایگان";
    public const string PanelTileFreeUseNoContact = "بی‌ارتباط";
    public const string PanelTileFreeUseLastContact = "آخرین ارتباط: {زمان}";
    public const string PanelTileFreeUseNoContactNote =
        "سرور بیش از ۳ دقیقه است از این سیستم خبر ندارد. زمان تا آخرین ارتباط حساب شده است.";
    public const string PanelTileFreeUseNoContactA11y = "⁨{PC}⁩، رایگان، بی‌ارتباط، {m} دقیقه";
    public const string PanelTileFreeUseA11y = "⁨{PC}⁩، رایگان، {m} دقیقه";
    public const string PanelFreeUseSheetTitle = "⁨{PC}⁩ — استفادهٔ رایگان";
    public const string PanelFreeUseSheetStart = "شروع: {HH:mm}";
    public const string PanelFreeUseSheetElapsed = "زمان گذشته: {HH:mm}";
    public const string PanelFreeUseSheetNote =
        "پایان استفادهٔ رایگان فقط با «قفل» روی خود سیستم است، یا خودکار بعد از {N} دقیقه بی‌حرکتی.";
    public const string PanelFreeUseSheetClose = "بستن";

    public const string MgrFreeIdleSentence = "قفل خودکار استفادهٔ رایگان بعد از {فیلد} دقیقه بی‌حرکتی";
    public const string MgrFreeIdleLabel = "قفل خودکار استفادهٔ رایگان بعد از چند دقیقه بی‌حرکتی";
    public const string MgrFreeIdleHelp = "پیش‌فرض ۵ دقیقه. عدد کامل از ۱ تا ۲۴۰.";
    public const string MgrFreeIdleApplyNote = "مقدار تازه با تماس بعدی هر کیوسک با سرور اعمال می‌شود، حتی روی استفاده‌های رایگانِ باز.";
    public const string MgrFreeIdleSave = "ذخیره";
    public const string MgrFreeIdleSaving = "در حال ذخیره…";
    public const string MgrFreeIdleSaved = "ذخیره شد.";
    public const string MgrFreeIdleErrEmpty = "تعداد دقیقه را وارد کنید.";
    public const string MgrFreeIdleErrInvalid = "یک عدد کامل از ۱ تا ۲۴۰ وارد کنید.";
    public const string MgrFreeIdleErrForbidden = "فقط مدیر یا مالک می‌تواند این را عوض کند.";
    public const string MgrFreeIdleErrSaveFail = "ذخیره نشد. دوباره امتحان کنید.";
    public const string MgrFreeIdleLoadFail = "تنظیم خوانده نشد.";
    public const string CommonRetry = "تلاش دوباره";

    public const string RepFreeUseTitle = "استفادهٔ رایگان کیوسک";
    public const string RepFreeUseBtn = "استفادهٔ رایگان کیوسک";
    public const string RepFreeUseNote = "این استفاده‌ها رایگان‌اند و در درآمد و جمع دریافتی حساب نمی‌شوند.";
    public const string RepFreeUseRestartNote =
        "برای «خاموش/ری‌استارت»، زمان پایان آخرین زمان فعالیت ثبت‌شدهٔ همان سیستم است.";
    public const string RepFreeUseColPc = "PC";
    public const string RepFreeUseColOperator = "اپراتور کیوسک";
    public const string RepFreeUseColStart = "شروع";
    public const string RepFreeUseColEnd = "پایان";
    public const string RepFreeUseColDuration = "مدت";
    public const string RepFreeUseColReason = "علت پایان";
    public const string RepFreeUseRoleOperator = "اپراتور کیوسک";
    public const string RepFreeUseRoleManager = "مدیر کیوسک";
    public const string RepFreeUseReasonManual = "دستی";
    public const string RepFreeUseReasonAuto = "خودکار";
    public const string RepFreeUseReasonRestart = "خاموش/ری‌استارت";
    public const string RepFreeUseReasonOpen = "در جریان";
    public const string RepFreeUseReasonNoContact = "بی‌ارتباط";
    public const string RepFreeUseDurationToLastContact = "{مدت} تا آخرین ارتباط";
    public const string RepFreeUseNoContactNote =
        "برای «بی‌ارتباط»، مدت تا آخرین ارتباط سیستم با سرور حساب شده است.";
    public const string RepFreeUseTotalNoContact =
        "({j} مورد بی‌ارتباط است و تا آخرین ارتباط حساب شده است.)";
    public const string RepFreeUseLoading = "در حال بارگذاری…";
    public const string RepFreeUseEmpty = "در این بازه استفادهٔ رایگانی ثبت نشده است.";
    public const string RepFreeUseError = "گزارش بارگذاری نشد. دوباره امتحان کنید.";
    public const string RepFreeUseBadRange = "تاریخ شروع بازه بعد از تاریخ پایان است.";
    public const string RepFreeUseForbidden = "این گزارش فقط برای مدیر یا مالک است.";
    public const string RepFreeUseDurHm = "{h} ساعت و {m} دقیقه";
    public const string RepFreeUseDurM = "{m} دقیقه";
    public const string RepFreeUseDurLt1 = "کمتر از ۱ دقیقه";
    public const string RepFreeUseDurSofar = "{مدت} تا الان";
    public const string RepFreeUseCardOperator = "اپراتور کیوسک: {…}";
    public const string RepFreeUseCardStart = "شروع: {…}";
    public const string RepFreeUseCardEnd = "پایان: {…}";
    public const string RepFreeUseCardDuration = "مدت: {…}";
    public const string RepFreeUseTotal = "{n} مورد — مجموع مدت: {مدت}";
    public const string RepFreeUseTotalOpen = "({k} مورد هنوز باز است و تا همین حالا حساب شده است.)";

    public const int MaxFreeUseIdleMinutes = 240;
}