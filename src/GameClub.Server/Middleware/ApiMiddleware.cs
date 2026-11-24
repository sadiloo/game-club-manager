using GameClub.Billing;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
            || context.Request.Path.StartsWithSegments("/api/local")
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

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
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
            logger.LogWarning(
                "BillingException {BillingCode} on {Method} {Path}",
                ex.Code,
                context.Request.Method,
                context.Request.Path);
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (BadHttpRequestException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted) throw;
            logger.LogError(
                ex,
                "Unhandled exception processing {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
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
        var path = context.Request.Path;
        var operatorClientShape = path.StartsWithSegments("/api/operator", StringComparison.OrdinalIgnoreCase)
                                  && !path.StartsWithSegments("/api/operator/auth", StringComparison.OrdinalIgnoreCase);
        if (operatorClientShape)
        {
            if (extras is null || extras.Count == 0)
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { code, message }));
                return;
            }

            var operatorPayload = new Dictionary<string, object?>(extras.Count + 2)
            {
                ["code"] = code,
                ["message"] = message,
            };
            foreach (var (key, value) in extras)
            {
                operatorPayload[key] = value;
            }

            await context.Response.WriteAsync(JsonSerializer.Serialize(operatorPayload));
            return;
        }

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
