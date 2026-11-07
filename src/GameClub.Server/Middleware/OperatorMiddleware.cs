using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server;

public sealed class OperatorSessionMiddleware(RequestDelegate next)
{
    public const string CookieName = "gameclub_operator";

    public async Task InvokeAsync(HttpContext context, AppDbContext db, OperatorContext operatorContext, IConfiguration config)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token))
        {
            var secret = config["OperatorSessionSecret"] ?? "dev-operator-secret";
            var userId = OperatorAuthService.ParseSessionToken(token, secret);
            if (userId.HasValue)
            {
                var user = await db.OperatorUsers.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == userId.Value && u.IsActive);
                if (user is not null)
                {
                    operatorContext.UserId = user.Id;
                    operatorContext.Role = user.Role;
                    context.Items["OperatorAuthenticated"] = true;
                }
            }
        }

        await next(context);
    }
}

public sealed class OperatorAuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/operator"))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api/operator/auth/login"))
        {
            await next(context);
            return;
        }

        if (context.Items["OperatorAuthenticated"] is true)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Operator login required.");
    }
}
