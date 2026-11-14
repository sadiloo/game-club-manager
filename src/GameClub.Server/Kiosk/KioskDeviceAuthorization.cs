using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Kiosk;

public static class KioskDeviceAuthorization
{
    public static ApiException ForeignStation(string detail)
        => new(StatusCodes.Status403Forbidden, "stale_station", detail);

    public static void EnsureStationMatches(HttpContext http, string requestedStationId)
    {
        if (!http.Items.TryGetValue("KioskStationId", out var sidObj) || sidObj is not string deviceStation)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "device_auth", "Kiosk device authentication required.");
        }

        var requested = StationIdNormalizer.Normalize(requestedStationId);
        if (!string.Equals(deviceStation, requested, StringComparison.Ordinal))
        {
            throw ForeignStation("Station does not match the paired device.");
        }
    }

    public static void EnsureSessionOnDeviceStation(PlaySessionEntity session, string deviceStationId)
    {
        if (!string.Equals(session.StationId, deviceStationId, StringComparison.Ordinal))
        {
            throw ForeignStation("Session is not on this device's station.");
        }
    }

    public static bool TryGetDeviceStation(HttpContext http, out string stationId)
    {
        stationId = "";
        if (http.Items.TryGetValue("KioskStationId", out var sidObj) && sidObj is string s)
        {
            stationId = s;
            return true;
        }

        return false;
    }
}
