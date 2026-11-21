using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Tests;

/// <summary>Test-only route that throws after <see cref="GameClub.Server.ApiExceptionMiddleware"/> so 500 mapping is exercised.</summary>
internal sealed class UnhandledExceptionTestStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/operator/_test/unhandled", StringComparison.Ordinal)
                && HttpMethods.IsGet(context.Request.Method))
            {
                throw new InvalidOperationException(
                    "internal diagnostic fault: must not appear in API response");
            }

            await nextMiddleware(context);
        });
    };
}