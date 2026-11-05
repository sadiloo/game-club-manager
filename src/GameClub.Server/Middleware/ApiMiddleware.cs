using GameClub.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GameClub.Server;

public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
{
    public const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/swagger"))
        {
            await next(context);
            return;
        }

        var expected = config["GAMECLUB_API_KEY"] ?? config["ApiKey"] ?? "dev-key";
        if (!context.Request.Headers.TryGetValue(HeaderName, out var provided) || provided != expected)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await next(context);
    }
}

public sealed class ApiExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            context.Response.StatusCode = ex.Status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Code, code = ex.Code, message = ex.Message }));
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsActiveSessionUniqueViolation(ex))
        {
            await WriteConflictAsync(context, "station_busy", "Station already has an open session.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsOpenPauseUniqueViolation(ex))
        {
            await WriteConflictAsync(context, "already_paused", "Session is already paused.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsInvoiceSessionUniqueViolation(ex))
        {
            await WriteConflictAsync(context, "closed", "Session is closed.");
        }
    }

    private static async Task WriteConflictAsync(HttpContext context, string code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = code, code, message }));
    }
}
