using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Kiosk;

/// <summary>Test-only: when set, runs immediately before <c>SessionService.StartAsync</c> on member attach (no open session).</summary>
internal static class KioskAttachPathTestSeam
{
    internal static Func<CancellationToken, Task<DbUpdateException>>? InjectBeforeMemberStartAsync { get; set; }
}