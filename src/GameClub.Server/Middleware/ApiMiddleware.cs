using GameClub.Server.Services;
using Microsoft.AspNetCore.Mvc;
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
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { code = ex.Code, message = ex.Message }));
        }
    }
}
