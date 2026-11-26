using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Guard;

/// <summary>Post-signature admission rules for signed internet commands (brief §6.2).</summary>
public static class InternetCommandAdmission
{
    public static string? TryRejectBeforeApply(
        InternetCommandPayload payload,
        string configuredStationId,
        Guid currentEpoch,
        long appliedVersion,
        InternetCommandPayload? applied)
    {
        if (!string.Equals(payload.StationId, configuredStationId, StringComparison.Ordinal))
        {
            return "rejected_station";
        }

        if (payload.Epoch == currentEpoch && payload.Version <= appliedVersion)
        {
            return "rejected_stale";
        }

        if (payload.Epoch != currentEpoch
            && payload.IssuedAtUtc <= (applied?.IssuedAtUtc ?? DateTime.MinValue))
        {
            return "rejected_stale";
        }

        return null;
    }
}