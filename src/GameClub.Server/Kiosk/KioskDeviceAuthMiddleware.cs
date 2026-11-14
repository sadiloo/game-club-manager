using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Kiosk;

public sealed class KioskDeviceAuthMiddleware(RequestDelegate next)
{
    public const string DeviceIdHeader = "X-Device-Id";
    public const string DeviceKeyHeader = "X-Device-Key";

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (!context.Request.Path.StartsWithSegments("/api/kiosk")
            && !context.Request.Path.StartsWithSegments("/hubs/kiosk"))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api/kiosk/pair")
            && HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(DeviceIdHeader, out var deviceIdRaw)
            || !Guid.TryParse(deviceIdRaw, out var deviceId)
            || !context.Request.Headers.TryGetValue(DeviceKeyHeader, out var deviceKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { code = "device_auth", message = "Device credentials required." });
            return;
        }

        var hash = KioskCrypto.HashDeviceKey(deviceKey!);
        var device = await db.KioskDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && !d.IsRevoked);
        if (device is null || !string.Equals(device.DeviceKeyHash, hash, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { code = "device_auth", message = "Invalid device credentials." });
            return;
        }

        context.Items["KioskDeviceId"] = deviceId;
        context.Items["KioskStationId"] = device.StationId;
        await next(context);
    }
}
