using System.Text;
using GameClub.Server.Diagnostics;

namespace GameClub.Server;

public sealed class ProbeApiRequestLoggingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!ProbeRequestLog.IsEnabled)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/kiosk", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/operator", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var requestUtc = DateTime.UtcNow;
        var requestMs = ProbeRequestLog.UtcMs();
        string? requestBody = null;
        if (context.Request.ContentLength > 0
            && context.Request.Body.CanRead
            && (HttpMethods.IsPost(context.Request.Method)
                || HttpMethods.IsPut(context.Request.Method)
                || HttpMethods.IsPatch(context.Request.Method)))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            requestBody = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
        }

        var originalBody = context.Response.Body;
        await using var capture = new MemoryStream();
        context.Response.Body = capture;

        await next(context);

        capture.Position = 0;
        string responseBody;
        using (var reader = new StreamReader(capture, Encoding.UTF8, leaveOpen: true))
        {
            responseBody = await reader.ReadToEndAsync();
        }

        capture.Position = 0;
        await capture.CopyToAsync(originalBody);
        context.Response.Body = originalBody;

        var responseUtc = DateTime.UtcNow;
        var responseMs = ProbeRequestLog.UtcMs();
        var station = context.Items.TryGetValue("KioskStationId", out var sid) ? sid?.ToString() : null;
        var stationPart = station is not null ? $" station={station}" : "";
        ProbeRequestLog.WriteLine(
            "PROBE_V3_API "
            + $"req_utc={requestUtc:O} req_ms={requestMs} "
            + $"res_utc={responseUtc:O} res_ms={responseMs} "
            + $"method={context.Request.Method} path={path} status={context.Response.StatusCode}{stationPart} "
            + $"req_body={ProbeRequestLog.RedactBody(requestBody)} "
            + $"res_body={ProbeRequestLog.RedactBody(responseBody)}");
    }
}