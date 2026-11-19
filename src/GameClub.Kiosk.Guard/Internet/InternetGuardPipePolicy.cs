using System;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetGuardPipePolicy
{
    internal const int DefaultReadTimeoutMs = GuardInternetClient.DefaultIoTimeoutMs;

    internal static int ResolveReadTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_READ_MS");
        return int.TryParse(raw, out var ms) && ms > 0 ? ms : DefaultReadTimeoutMs;
    }
}