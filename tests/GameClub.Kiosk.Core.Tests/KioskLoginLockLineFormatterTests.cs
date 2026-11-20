using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

/// <summary>DEF-4: lock-line login error formatter and credential hygiene.</summary>
public sealed class KioskLoginLockLineFormatterTests
{
    private const string LockBase = "Locked — PC-1 · no server response";

    [Fact]
    public void lock_line_appends_http_status_login_error()
    {
        var line = KioskLoginUserMessages.AppendToLockLine(LockBase, KioskLoginUserMessages.HttpStatus(409));
        Assert.Contains("Login failed (HTTP 409)", line, StringComparison.Ordinal);
        Assert.StartsWith(LockBase, line, StringComparison.Ordinal);
    }

    [Fact]
    public void lock_line_appends_unreachable_login_error()
    {
        var line = KioskLoginUserMessages.AppendToLockLine(LockBase, KioskLoginUserMessages.Unreachable);
        Assert.Contains(KioskLoginUserMessages.Unreachable, line, StringComparison.Ordinal);
    }

    [Fact]
    public void lock_line_appends_timeout_login_error()
    {
        var line = KioskLoginUserMessages.AppendToLockLine(LockBase, KioskLoginUserMessages.Timeout);
        Assert.Contains(KioskLoginUserMessages.Timeout, line, StringComparison.Ordinal);
    }

    [Fact]
    public void login_error_survives_lock_line_refresh_cycle()
    {
        var first = KioskLoginUserMessages.AppendToLockLine(LockBase, KioskLoginUserMessages.HttpStatus(503));
        var second = KioskLoginUserMessages.AppendToLockLine(LockBase, KioskLoginUserMessages.HttpStatus(503));
        Assert.Equal(first, second);
    }

    [Fact]
    public void login_error_never_contains_password_or_device_key()
    {
        const string password = "s3cret-pass";
        const string deviceKey = "device-key-abcdef";
        foreach (var error in new[]
                 {
                     KioskLoginUserMessages.Unreachable,
                     KioskLoginUserMessages.Timeout,
                     KioskLoginUserMessages.HttpStatus(409),
                 })
        {
            var line = KioskLoginUserMessages.AppendToLockLine(LockBase, error);
            Assert.False(KioskLoginUserMessages.ExposesCredential(line, password, deviceKey));
        }
    }
}