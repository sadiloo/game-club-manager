namespace GameClub.Kiosk.Core.Login;

/// <summary>Persian customer login errors (spec v2.9 §6.5 / §10).</summary>
public static class KioskLoginStrings
{
    public const string EmptyFields = "نام کاربری و رمز را وارد کنید.";
    public const string PairBefore = "این سیستم هنوز آماده نیست. لطفاً به پیشخوان خبر بدهید.";
    public const string Unreachable =
        "به سرور وصل نشد. چند لحظه بعد دوباره امتحان کنید؛ اگر درست نشد، به پیشخوان خبر بدهید.";
    public const string Timeout = "سرور دیر جواب داد. لطفاً دوباره امتحان کنید.";
    public const string HttpGeneric =
        "ورود انجام نشد. دوباره امتحان کنید؛ اگر تکرار شد، به پیشخوان خبر بدهید.";
    public const string Unknown = "مشکلی پیش آمد. لطفاً به پیشخوان خبر بدهید.";
    public const string InvalidLogin = "نام کاربری یا رمز اشتباه است.";
    public const string DeviceAuth = "این سیستم به سرور شناسانده نشده است. لطفاً به پیشخوان خبر بدهید.";
    public const string AccountLocked =
        "به‌خاطر چند رمز اشتباه، حساب شما قفل شد. برای باز شدنش به پیشخوان بروید.";
    public const string NoCredit = "اعتبار شما برای بازی کافی نیست. برای شارژ به پیشخوان بروید.";
    public const string DebtBlocked = "حساب شما بدهی دارد. لطفاً اول در پیشخوان تسویه کنید.";
    public const string StaleStation = "این سیستم درست تنظیم نشده است. لطفاً به پیشخوان خبر بدهید.";
    public const string SessionElsewhere =
        "شما روی سیستم دیگری بازی باز دارید. برای جابه‌جایی به پیشخوان بروید.";
    public const string TestMode = "این سیستم الان در حالت آزمایش است. لطفاً به پیشخوان خبر بدهید.";
    public const string StationBusy = "این سیستم دست مشتری دیگری است. لطفاً به پیشخوان خبر بدهید.";

    public static string ForServerCode(string? code)
        => code switch
        {
            "invalid_login" => InvalidLogin,
            "device_auth" => DeviceAuth,
            "account_locked" => AccountLocked,
            "no_credit" => NoCredit,
            "debt_blocked" => DebtBlocked,
            "stale_station" => StaleStation,
            "session_elsewhere" => SessionElsewhere,
            "test_mode" => TestMode,
            "station_busy" => StationBusy,
            _ => Unknown,
        };
}