using System;
using System.IO;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

/// <summary>
/// Pins LoadOfflineHtml behaviour for webview-recovery-exhausted (via <see cref="OfflineHtmlContent.ApplyState"/>).
/// </summary>
public sealed class WebViewRecoveryExhaustedOfflineHtmlPinTests
{
    private static string OfflineTemplatePath =>
        Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../src/GameClub.Shell/Assets/offline.html"));

    [Fact]
    public void webview_recovery_exhausted_offline_html_shows_panel_down_reload_instruction_and_hides_countdown()
    {
        var template = File.ReadAllText(OfflineTemplatePath);
        var html = OfflineHtmlContent.ApplyState(template, "webview-recovery-exhausted");

        Assert.Contains("پنل اپراتور از کار افتاد", html, StringComparison.Ordinal);
        Assert.Contains(
            "روی آیکن چک‌پوینت کنار ساعت راست‌کلیک کنید و از منو «بازخوانی» را بزنید.",
            html,
            StringComparison.Ordinal);
        Assert.Contains("id=\"countdown\" style=\"display:none\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "سرور در دسترس نیست؛ دوباره تلاش می‌شود…",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<p id=\"countdown\"></p>", html, StringComparison.Ordinal);
    }
}