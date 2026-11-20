using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

/// <summary>DEF-4: lock-line login error formatter and credential hygiene.</summary>
public sealed class KioskLoginLockLineFormatterTests
{
    private const string LockBase = "Locked — PC-1 · no server response";

    [Fact]
    public void lock_line_appends_http_generic_login_error_placeholder()
    {
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.HttpGeneric);
        var line = KioskLoginUserMessages.AppendToLockLine(LockBase, error);
        Assert.Contains(error, line, StringComparison.Ordinal);
        Assert.StartsWith(LockBase, line, StringComparison.Ordinal);
    }

    [Fact]
    public void lock_line_appends_unreachable_login_error_placeholder()
    {
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.Unreachable);
        var line = KioskLoginUserMessages.AppendToLockLine(LockBase, error);
        Assert.Contains(error, line, StringComparison.Ordinal);
    }

    [Fact]
    public void lock_line_appends_timeout_login_error_placeholder()
    {
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.Timeout);
        var line = KioskLoginUserMessages.AppendToLockLine(LockBase, error);
        Assert.Contains(error, line, StringComparison.Ordinal);
    }

    [Fact]
    public void login_error_survives_lock_line_refresh_cycle()
    {
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.StationBusy);
        var first = KioskLoginUserMessages.AppendToLockLine(LockBase, error);
        var second = KioskLoginUserMessages.AppendToLockLine(LockBase, error);
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
            var error = KioskLoginUserMessages.DisplayText(key);
            var line = KioskLoginUserMessages.AppendToLockLine(LockBase, error);
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