using GameClub.Billing;
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

        if (context.Request.Path.StartsWithSegments("/swagger")
            || context.Request.Path.StartsWithSegments("/operator")
            || context.Request.Path.StartsWithSegments("/api/operator/auth/login")
            || context.Request.Path.StartsWithSegments("/api/kiosk")
            || context.Request.Path.StartsWithSegments("/hubs/kiosk"))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api/operator")
            && context.Items["OperatorAuthenticated"] is true)
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/hubs/operator")
            && context.Items["OperatorAuthenticated"] is true)
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
            await WriteJsonErrorAsync(context, ex.Status, ex.Code, ex.Message, ex.Extras);
        }
        catch (BillingException ex)
        {
            var userMessage = BillingExceptionUserMessages.ForCode(ex.Code);
            await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, ex.Code, userMessage, null);
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
        catch (Exception)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Internal Server Error");
        }
    }

    private static async Task WriteConflictAsync(HttpContext context, string code, string message)
    {
        await WriteJsonErrorAsync(context, StatusCodes.Status409Conflict, code, message, null);
    }

    private static async Task WriteJsonErrorAsync(
        HttpContext context,
        int status,
        string code,
        string message,
        IReadOnlyDictionary<string, object>? extras)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var payload = new Dictionary<string, object?>
        {
            ["error"] = code,
            ["code"] = code,
            ["message"] = message,
        };
        if (extras is not null)
        {
            foreach (var (key, value) in extras)
            {
                payload[key] = value;
            }
        }

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
