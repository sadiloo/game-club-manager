using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Kiosk;

/// <summary>Test-only: per async-flow hook before <c>SessionService.StartAsync</c> on member attach (no open session).</summary>
internal static class KioskAttachPathTestSeam
{
    private static readonly AsyncLocal<Func<CancellationToken, Task<DbUpdateException>>?> InjectBeforeMemberStartAsyncFlow = new();

    internal static Func<CancellationToken, Task<DbUpdateException>>? InjectBeforeMemberStartAsync
    {
        get => InjectBeforeMemberStartAsyncFlow.Value;
        set => InjectBeforeMemberStartAsyncFlow.Value = value;
    }

    internal static bool TryTakeInjectForCurrentFlow(out Func<CancellationToken, Task<DbUpdateException>> inject)
    {
        var hook = InjectBeforeMemberStartAsyncFlow.Value;
        if (hook is null)
        {
            inject = null!;
            return false;
        }

        InjectBeforeMemberStartAsyncFlow.Value = null;
        inject = hook;
        return true;
    }
}