using System.Text.RegularExpressions;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskCustomerLoginMessageMappingTests
{
    private static readonly IReadOnlyDictionary<string, string> SpecSection65 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [KioskLoginUserMessages.Keys.EmptyFields] = "نام کاربری و رمز را وارد کنید.",
        [KioskLoginUserMessages.Keys.PairBefore] = "این سیستم هنوز آماده نیست. لطفاً به پیشخوان خبر بدهید.",
        [KioskLoginUserMessages.Keys.Unreachable] = "به سرور وصل نشد. چند لحظه بعد دوباره امتحان کنید؛ اگر درست نشد، به پیشخوان خبر بدهید.",
        [KioskLoginUserMessages.Keys.Timeout] = "سرور دیر جواب داد. لطفاً دوباره امتحان کنید.",
        [KioskLoginUserMessages.Keys.HttpGeneric] = "ورود انجام نشد. دوباره امتحان کنید؛ اگر تکرار شد، به پیشخوان خبر بدهید.",
        [KioskLoginUserMessages.Keys.Unknown] = "مشکلی پیش آمد. لطفاً به پیشخوان خبر بدهید.",
        [KioskLoginUserMessages.Keys.InvalidLogin] = "نام کاربری یا رمز اشتباه است.",
        [KioskLoginUserMessages.Keys.DeviceAuth] = "این سیستم به سرور شناسانده نشده است. لطفاً به پیشخوان خبر بدهید.",
        [KioskLoginUserMessages.Keys.AccountLocked] = "به‌خاطر چند رمز اشتباه، حساب شما قفل شد. برای باز شدنش به پیشخوان بروید.",
        [KioskLoginUserMessages.Keys.NoCredit] = "اعتبار شما برای بازی کافی نیست. برای شارژ به پیشخوان بروید.",
        [KioskLoginUserMessages.Keys.DebtBlocked] = "حساب شما بدهی دارد. لطفاً اول در پیشخوان تسویه کنید.",
        [KioskLoginUserMessages.Keys.StaleStation] = "این سیستم درست تنظیم نشده است. لطفاً به پیشخوان خبر بدهید.",
        [KioskLoginUserMessages.Keys.SessionElsewhere] = "شما روی سیستم دیگری بازی باز دارید. برای جابه‌جایی به پیشخوان بروید.",
        [KioskLoginUserMessages.Keys.TestMode] = "این سیستم الان در حالت آزمایش است. لطفاً به پیشخوان خبر بدهید.",
        [KioskLoginUserMessages.Keys.StationBusy] = "این سیستم دست مشتری دیگری است. لطفاً به پیشخوان خبر بدهید.",
    };

    private static readonly Regex AsciiLetters = new("[A-Za-z]", RegexOptions.CultureInvariant);

    [Fact]
    public void display_text_matches_spec_section_6_5_exactly()
    {
        Assert.Equal(SpecSection65.Count, KioskLoginUserMessages.MappedKeys.Count);
        foreach (var (key, expected) in SpecSection65)
        {
            Assert.Equal(expected, KioskLoginUserMessages.DisplayText(key));
        }
    }

    [Fact]
    public void every_mapped_key_maps_to_its_own_display_text_without_latin_letters()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in KioskLoginUserMessages.MappedKeys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            var text = KioskLoginUserMessages.DisplayText(key);
            Assert.False(AsciiLetters.IsMatch(text), $"Latin letters in display text for {key}");
            Assert.True(seen.Add(text), $"duplicate display text for key {key}");
        }

        Assert.Equal(KioskLoginUserMessages.MappedKeys.Count, seen.Count);
    }

    [Theory]
    [InlineData(KioskLoginUserMessages.Keys.InvalidLogin, """{"code":"invalid_login","message":"Invalid login or password."}""")]
    [InlineData(KioskLoginUserMessages.Keys.DeviceAuth, """{"code":"device_auth","message":"Kiosk device authentication required."}""")]
    [InlineData(KioskLoginUserMessages.Keys.AccountLocked, """{"code":"account_locked","message":"Account locked; operator reset required."}""")]
    [InlineData(KioskLoginUserMessages.Keys.NoCredit, """{"code":"no_credit","message":"Insufficient credit for PC play."}""")]
    [InlineData(KioskLoginUserMessages.Keys.DebtBlocked, """{"code":"debt_blocked","message":"PC play is blocked by shared wallet debt."}""")]
    [InlineData(KioskLoginUserMessages.Keys.StaleStation, """{"code":"stale_station","message":"Station does not match the paired device."}""")]
    [InlineData(KioskLoginUserMessages.Keys.SessionElsewhere, """{"code":"session_elsewhere","message":"Customer already has an open PC session."}""")]
    [InlineData(KioskLoginUserMessages.Keys.TestMode, """{"code":"test_mode","message":"Station is in test mode."}""")]
    [InlineData(KioskLoginUserMessages.Keys.StationBusy, """{"code":"station_busy","message":"Station has another customer's session."}""")]
    public void each_server_semantic_code_resolves_to_its_message_key(string expectedKey, string body)
    {
        var key = KioskLoginUserMessages.ResolveFailureKey(body, 418);
        Assert.Equal(expectedKey, key);
        Assert.Equal(SpecSection65[expectedKey], KioskLoginUserMessages.DisplayText(key));
        Assert.DoesNotMatch(AsciiLetters, KioskLoginUserMessages.DisplayText(key));
    }

    [Theory]
    [InlineData(KioskLoginUserMessages.Keys.PairBefore)]
    [InlineData(KioskLoginUserMessages.Keys.Unreachable)]
    [InlineData(KioskLoginUserMessages.Keys.Timeout)]
    [InlineData(KioskLoginUserMessages.Keys.HttpGeneric)]
    [InlineData(KioskLoginUserMessages.Keys.EmptyFields)]
    public void local_key_display_matches_spec(string key)
    {
        Assert.Equal(SpecSection65[key], KioskLoginUserMessages.DisplayText(key));
    }

    [Fact]
    public void unknown_server_code_maps_to_unknown_key_not_http_generic()
    {
        const string body = """{"code":"not_a_real_code","message":"x"}""";
        var key = KioskLoginUserMessages.ResolveFailureKey(body, 418);
        Assert.Equal(KioskLoginUserMessages.Keys.Unknown, key);
        Assert.Equal(SpecSection65[KioskLoginUserMessages.Keys.Unknown], KioskLoginUserMessages.DisplayText(key));
    }

    [Fact]
    public void server_semantic_code_wins_over_http_status_bucket()
    {
        const string body = """{"code":"invalid_login","message":"Invalid login or password."}""";
        var key = KioskLoginUserMessages.ResolveFailureKey(body, 401);
        Assert.Equal(KioskLoginUserMessages.Keys.InvalidLogin, key);
    }

    [Fact]
    public void http_failure_without_parseable_code_uses_http_generic_key()
    {
        var key = KioskLoginUserMessages.ResolveFailureKey(responseBody: null, httpStatus: 503);
        Assert.Equal(KioskLoginUserMessages.Keys.HttpGeneric, key);
    }

    [Theory]
    [InlineData(KioskLoginUserMessages.LoginTransportFailure.Unreachable, KioskLoginUserMessages.Keys.Unreachable)]
    [InlineData(KioskLoginUserMessages.LoginTransportFailure.Timeout, KioskLoginUserMessages.Keys.Timeout)]
    public void transport_failure_maps_before_http(
        KioskLoginUserMessages.LoginTransportFailure transport,
        string expectedKey)
    {
        const string body = """{"code":"invalid_login"}""";
        var key = KioskLoginUserMessages.ResolveFailureKey(body, 401, transport);
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("", "pass")]
    [InlineData("user", "")]
    [InlineData("   ", "pass")]
    [InlineData("user", "   ")]
    public void empty_or_whitespace_credentials_map_to_empty_fields_key(string login, string password)
    {
        var key = KioskLoginUserMessages.TryGetEmptyFieldsKey(login, password);
        Assert.Equal(KioskLoginUserMessages.Keys.EmptyFields, key);
    }

    [Fact]
    public void non_empty_credentials_pass_empty_fields_gate()
    {
        Assert.Null(KioskLoginUserMessages.TryGetEmptyFieldsKey("ali", "secret"));
    }

    [Fact]
    public void login_error_survives_lock_line_refresh_cycle()
    {
        var lockState = KioskStationStatus.FormatLocalLockScreenState(
            "PC-1",
            leaseExpired: false,
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            consecutiveHeartbeatFailures: 2,
            lastHeartbeatAckUtc: DateTime.UtcNow,
            utcNow: DateTime.UtcNow,
            lastErrorDetail: null);
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.Timeout);
        var first = KioskStatusPresentationBuilder.Build(lockState, error, null).ToCombinedText();
        var second = KioskStatusPresentationBuilder.Build(lockState, error, null).ToCombinedText();
        Assert.Equal(first, second);
    }
}