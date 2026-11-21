using System.Collections.Concurrent;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Kiosk;

/// <summary>Kiosk manager password checks during first-run setup; not shared with panel login lock (C).</summary>
public sealed class KioskSetupLockTracker
{
    public const int WrongLimit = 2;
    public const int LockMinutes = 15;

    private sealed class IpState
    {
        public int WrongCount;
        public DateTime? LockedUntilUtc;
    }

    private readonly ConcurrentDictionary<string, IpState> _byIp = new(StringComparer.Ordinal);

    public bool IsLocked(string clientIp, DateTime utcNow, out DateTime? lockedUntilUtc)
    {
        var state = _byIp.GetOrAdd(NormalizeIp(clientIp), _ => new IpState());
        lockedUntilUtc = state.LockedUntilUtc;
        if (lockedUntilUtc is null)
        {
            return false;
        }

        if (utcNow >= lockedUntilUtc.Value)
        {
            state.LockedUntilUtc = null;
            state.WrongCount = 0;
            lockedUntilUtc = null;
            return false;
        }

        return true;
    }

    public void RegisterFailure(string clientIp, DateTime utcNow)
    {
        var state = _byIp.GetOrAdd(NormalizeIp(clientIp), _ => new IpState());
        if (state.LockedUntilUtc is not null && utcNow < state.LockedUntilUtc.Value)
        {
            return;
        }

        state.WrongCount++;
        if (state.WrongCount >= WrongLimit)
        {
            state.LockedUntilUtc = utcNow.AddMinutes(LockMinutes);
        }
    }

    public void RegisterSuccess(string clientIp)
    {
        var key = NormalizeIp(clientIp);
        if (_byIp.TryGetValue(key, out var state))
        {
            state.WrongCount = 0;
            state.LockedUntilUtc = null;
        }
    }

    public static int RetryAfterSeconds(DateTime utcNow, DateTime lockedUntilUtc)
        => Math.Max(1, (int)Math.Ceiling((lockedUntilUtc - utcNow).TotalSeconds));

    public void ThrowIfLocked(string clientIp, DateTime utcNow)
    {
        if (!IsLocked(clientIp, utcNow, out var until) || until is null)
        {
            return;
        }

        var seconds = RetryAfterSeconds(utcNow, until.Value);
        throw new ApiException(
            StatusCodes.Status429TooManyRequests,
            "kiosk_locked",
            "به‌خاطر ۲ رمز اشتباه، تا ۱۵ دقیقه قفل است.")
        {
            Extras = new Dictionary<string, object> { ["retryAfterSeconds"] = seconds },
        };
    }

    private static string NormalizeIp(string ip)
        => string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();
}