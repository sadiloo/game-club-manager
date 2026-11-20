using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

/// <summary>DEF-4: lock-line login error formatter and credential hygiene.</summary>
public sealed class KioskLoginLockLineFormatterTests
{
    private static readonly KioskStatusLineState LockBaseState = KioskStationStatus.FormatLocalLockScreenState(
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

    private static string WithLoginError(string errorKey)
    {
        var error = KioskLoginUserMessages.DisplayText(errorKey);
        return KioskStatusPresentationBuilder.Build(LockBaseState, error, null).ToCombinedText();
    }

    [Fact]
    public void lock_line_appends_http_generic_login_error_text()
    {
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.HttpGeneric);
        var line = WithLoginError(KioskLoginUserMessages.Keys.HttpGeneric);
        Assert.Contains(error, line, StringComparison.Ordinal);
        Assert.StartsWith(
            KioskStatusPresentationBuilder.Build(LockBaseState, null, null).ToCombinedText(),
            line,
            StringComparison.Ordinal);
    }

    [Fact]
    public void lock_line_appends_unreachable_login_error_text()
    {
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.Unreachable);
        var line = WithLoginError(KioskLoginUserMessages.Keys.Unreachable);
        Assert.Contains(error, line, StringComparison.Ordinal);
    }

    [Fact]
    public void lock_line_appends_timeout_login_error_text()
    {
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.Timeout);
        var line = WithLoginError(KioskLoginUserMessages.Keys.Timeout);
        Assert.Contains(error, line, StringComparison.Ordinal);
    }

    [Fact]
    public void login_error_survives_lock_line_refresh_cycle()
    {
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.StationBusy);
        var first = WithLoginError(KioskLoginUserMessages.Keys.StationBusy);
        var second = WithLoginError(KioskLoginUserMessages.Keys.StationBusy);
        Assert.Equal(first, second);
    }

    [Fact]
    public void login_error_never_contains_password_or_device_key()
    {
        const string password = "s3cret-pass";
        const string deviceKey = "device-key-abcdef";
        foreach (var key in new[]
                 {
                     KioskLoginUserMessages.Keys.Unreachable,
                     KioskLoginUserMessages.Keys.Timeout,
                     KioskLoginUserMessages.Keys.StationBusy,
                 })
        {
            var line = WithLoginError(key);
            Assert.False(KioskLoginUserMessages.ExposesCredential(line, password, deviceKey));
        }
    }

    [Fact]
    public void empty_fields_gate_means_login_http_is_not_sent()
    {
        Assert.False(KioskLoginUserMessages.LoginHttpWouldBeSent(isPaired: true, loginName: "", password: "x"));
        Assert.False(KioskLoginUserMessages.LoginHttpWouldBeSent(isPaired: true, loginName: "u", password: "  "));
        Assert.True(KioskLoginUserMessages.LoginHttpWouldBeSent(isPaired: true, loginName: "u", password: "p"));
    }
}