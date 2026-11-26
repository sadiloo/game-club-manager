namespace GameClub.Shell.Shell;

public static class OfflineHtmlContent
{
    public static string ApplyState(string html, string state)
    {
        html = html.Replace("state=stopped", "state=" + state);
        if (string.Equals(state, "panel-unavailable", StringComparison.Ordinal))
        {
            html = html.Replace(
                "سرور در دسترس نیست؛ دوباره تلاش می‌شود…",
                "سرور روشن است؛ پنل در دسترس نیست");
            html = html.Replace(
                "برای روشن کردن سرور، روی آیکن چک‌پوینت کنار ساعت ویندوز راست‌کلیک کنید و «شروع سرور» را بزنید.",
                "چند لحظه صبر کنید. پنل دوباره بارگذاری می‌شود.");
        }
        else if (string.Equals(state, "webview-recovery-exhausted", StringComparison.Ordinal))
        {
            html = html.Replace(
                "سرور در دسترس نیست؛ دوباره تلاش می‌شود…",
                "پنل اپراتور از کار افتاد");
            html = html.Replace(
                "برای روشن کردن سرور، روی آیکن چک‌پوینت کنار ساعت ویندوز راست‌کلیک کنید و «شروع سرور» را بزنید.",
                "روی آیکن چک‌پوینت کنار ساعت راست‌کلیک کنید و از منو «بازخوانی» را بزنید.");
            html = html.Replace(
                "<p id=\"countdown\"></p>",
                "<p id=\"countdown\" style=\"display:none\"></p>");
        }

        return html;
    }
}